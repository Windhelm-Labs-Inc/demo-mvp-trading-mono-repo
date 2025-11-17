using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Utilities;

namespace MarketMakerWorkerService.Services;

/// <summary>
/// Basic market making strategy that maintains a shaped liquidity ladder (100/50/50/10×7)
/// Processes index price updates and manages order placements with minimal changes
/// </summary>
public class BasicMarketMakerStrategy
{
    private readonly MarketMakerConfiguration _config;
    private readonly IAuthenticationService _authService;
    private readonly IOrderService _orderService;
    private readonly IAccountService _accountService;
    private readonly OrderStateManager _stateManager;
    private readonly ILogger<BasicMarketMakerStrategy> _logger;
    
    private readonly SemaphoreSlim _strategyLock = new(1, 1);
    private bool _isInitialized = false;
    private LiquidityShape _liquidityShape;

    public BasicMarketMakerStrategy(
        IOptions<MarketMakerConfiguration> config,
        IAuthenticationService authService,
        IOrderService orderService,
        IAccountService accountService,
        OrderStateManager stateManager,
        ILogger<BasicMarketMakerStrategy> logger)
    {
        _config = config.Value;
        _authService = authService;
        _orderService = orderService;
        _accountService = accountService;
        _stateManager = stateManager;
        _logger = logger;
        
        // Use default shaped liquidity (100/50/50/10×7)
        _liquidityShape = LiquidityShapeCalculator.DefaultShape;
    }

    /// <summary>
    /// Initialize the strategy (setup ladder structure)
    /// </summary>
    public void Initialize()
    {
        const int numLevels = 10; // Fixed at 10 levels per side
        
        _logger.LogInformation("Initializing BasicMarketMakerStrategy");
        _logger.LogInformation("Configuration: Spread=${SpreadUsd:F2}, LevelSpacing=${LevelSpacingUsd:F2}, NumLevels={NumLevels}",
            _config.BaseSpreadUsd, _config.LevelSpacingUsd, numLevels);
        _logger.LogInformation("Liquidity Shape: Level0={L0}, Levels1-2={L12}, Levels3-9={L39}, Total={Total} per side",
            _liquidityShape.Level0Size, _liquidityShape.Level1_2Size, _liquidityShape.Level3_9Size, _liquidityShape.TotalSize);
        
        _stateManager.InitializeLadder(numLevels);
        _isInitialized = true;
        
        _logger.LogInformation("BasicMarketMakerStrategy initialized successfully");
    }

    /// <summary>
    /// Process an index price update and manage the order ladder
    /// This is the main entry point called when Redis emits a new price
    /// </summary>
    public async Task OnIndexPriceUpdateAsync(decimal indexPrice, CancellationToken cancellationToken)
    {
        if (!_isInitialized)
        {
            _logger.LogWarning("Strategy not initialized, skipping price update");
            return;
        }

        await _strategyLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Processing index price update: ${Price:F2}", indexPrice);
            
            // Get authentication token
            var token = await _authService.GetValidTokenAsync(cancellationToken);
            
            // Calculate new price levels using FIXED USD spread and spacing
            const int numLevels = 10;
            var midPriceBase = PriceCalculator.ToBaseUnits(indexPrice, _config.TradingDecimals);
            var bidPrices = PriceCalculator.CalculateBidLevelsUsd(
                midPriceBase,
                _config.BaseSpreadUsd,
                _config.LevelSpacingUsd,
                numLevels,
                _config.TradingDecimals);
            var askPrices = PriceCalculator.CalculateAskLevelsUsd(
                midPriceBase,
                _config.BaseSpreadUsd,
                _config.LevelSpacingUsd,
                numLevels,
                _config.TradingDecimals);
            
            // Calculate quantities for shaped liquidity
            var quantities = LiquidityShapeCalculator.CalculateQuantities(
                _liquidityShape,
                _config.TradingDecimals,
                numLevels);
            
            // Determine which orders need to be replaced (minimal changes)
            var replacements = _stateManager.CalculateReplacements(
                bidPrices,
                askPrices,
                quantities);
            
            if (replacements.Count == 0)
            {
                _logger.LogDebug("No order changes needed (price within tolerance)");
                return;
            }
            
            _logger.LogInformation("Need to replace {Count} orders", replacements.Count);
            
            // Execute order replacements
            await ExecuteReplacementsAsync(replacements, token, cancellationToken);
            
            _logger.LogInformation("Successfully processed price update: ${Price:F2}", indexPrice);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing index price update: ${Price:F2}", indexPrice);
            throw;
        }
        finally
        {
            _strategyLock.Release();
        }
    }

    /// <summary>
    /// Execute order replacements: cancel old orders, submit new ones
    /// Uses minimal change strategy - only replaces what's necessary
    /// </summary>
    private async Task ExecuteReplacementsAsync(
        List<OrderReplacement> replacements,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        var cancelTasks = new List<Task>();
        var successfulCancels = new List<OrderReplacement>();
        
        // Step 1: Cancel old orders (if they exist)
        foreach (var replacement in replacements.Where(r => r.OldOrderId.HasValue))
        {
            try
            {
                _logger.LogDebug("Cancelling {Side} order at level {Level}: {OrderId}",
                    replacement.Side, replacement.LevelIndex, replacement.OldOrderId);
                
                // Safe to use .Value because of the HasValue filter above
                var orderIdToCancel = replacement.OldOrderId!.Value;
                await _orderService.CancelOrderAsync(
                    orderIdToCancel,
                    jwtToken,
                    cancellationToken);
                
                // Clear the level in state manager
                _stateManager.ClearLevel(replacement.Side, replacement.LevelIndex);
                successfulCancels.Add(replacement);
                
                // Rate limiting delay between cancels
                if (_config.RateLimitDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.RateLimitDelaySeconds), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel order {OrderId} at level {Level}",
                    replacement.OldOrderId, replacement.LevelIndex);
                // Continue with other cancellations
            }
        }
        
        // Step 2: Submit new orders
        foreach (var replacement in replacements)
        {
            try
            {
                _logger.LogDebug("Submitting {Side} order at level {Level}: Price={Price}, Qty={Quantity}",
                    replacement.Side, replacement.LevelIndex, replacement.NewPrice, replacement.NewQuantity);
                
                var response = await _orderService.SubmitLimitOrderAsync(
                    side: replacement.Side,
                    price: replacement.NewPrice,
                    quantity: replacement.NewQuantity,
                    marginFactor: (ulong)(_config.InitialMarginFactor * 1_000_000), // Convert decimal to base units
                    clientOrderId: $"MM-{replacement.Side}-L{replacement.LevelIndex}-{DateTime.UtcNow.Ticks}",
                    jwtToken: jwtToken,
                    cancellationToken: cancellationToken);
                
                // Update state manager with new order
                _stateManager.UpdateLevel(
                    replacement.Side,
                    replacement.LevelIndex,
                    response.OrderId,
                    replacement.NewPrice,
                    replacement.NewQuantity);
                
                _logger.LogInformation("✓ Placed {Side} order at level {Level}: OrderId={OrderId}",
                    replacement.Side, replacement.LevelIndex, response.OrderId);
                
                // Rate limiting delay between submits
                if (_config.RateLimitDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.RateLimitDelaySeconds), cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to submit order at {Side} level {Level}",
                    replacement.Side, replacement.LevelIndex);
                // Continue with other submissions
            }
        }
    }

    /// <summary>
    /// Check account balance and ensure sufficient capital
    /// </summary>
    public async Task<bool> HasSufficientCapitalAsync(decimal indexPrice, CancellationToken cancellationToken)
    {
        try
        {
            const int numLevels = 10;
            var token = await _authService.GetValidTokenAsync(cancellationToken);
            var snapshot = await _accountService.GetAccountSnapshotAsync(token, cancellationToken);
            
            var midPriceBase = PriceCalculator.ToBaseUnits(indexPrice, _config.TradingDecimals);
            var bidPrices = PriceCalculator.CalculateBidLevels(
                midPriceBase, _config.BaseSpreadBps, _config.LevelSpacingBps, numLevels);
            var askPrices = PriceCalculator.CalculateAskLevels(
                midPriceBase, _config.BaseSpreadBps, _config.LevelSpacingBps, numLevels);
            
            var hasSufficient = LiquidityShapeCalculator.HasSufficientCapital(
                _liquidityShape,
                bidPrices,
                askPrices,
                snapshot.Balance,
                (ulong)(_config.InitialMarginFactor * 1_000_000), // Convert decimal to base units
                _config.TradingDecimals,
                _config.SettlementDecimals,
                _config.BalanceUtilization);
            
            if (!hasSufficient)
            {
                _logger.LogWarning("Insufficient capital: Balance={Balance}, Required for full ladder",
                    snapshot.Balance);
            }
            
            return hasSufficient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking capital sufficiency");
            return false;
        }
    }

    /// <summary>
    /// Emergency stop - cancel all orders
    /// </summary>
    public async Task EmergencyStopAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("Emergency stop initiated - cancelling all orders");
        
        await _strategyLock.WaitAsync(cancellationToken);
        try
        {
            var token = await _authService.GetValidTokenAsync(cancellationToken);
            var activeOrderIds = _stateManager.GetAllActiveOrderIds();
            
            _logger.LogInformation("Cancelling {Count} active orders", activeOrderIds.Length);
            
            foreach (var orderId in activeOrderIds)
            {
                try
                {
                    await _orderService.CancelOrderAsync(orderId, token, cancellationToken);
                    var (side, level) = _stateManager.FindOrderLevel(orderId);
                    if (side.HasValue && level.HasValue)
                    {
                        _stateManager.ClearLevel(side.Value, level.Value);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to cancel order {OrderId} during emergency stop", orderId);
                }
            }
            
            _logger.LogInformation("Emergency stop completed");
        }
        finally
        {
            _strategyLock.Release();
        }
    }
}

