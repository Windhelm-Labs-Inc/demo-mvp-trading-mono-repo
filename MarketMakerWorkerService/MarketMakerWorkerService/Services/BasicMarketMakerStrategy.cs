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
        
        // Use configured liquidity shape
        _liquidityShape = new LiquidityShape
        {
            Level0Size = _config.Level0Quantity,
            Level1_2Size = _config.Levels1To2Quantity,
            Level3_9Size = _config.Levels3To9Quantity
        };
    }

    /// <summary>
    /// Initialize the strategy (setup ladder structure)
    /// </summary>
    public void Initialize()
    {
        var numLevels = _config.NumberOfLevels; // Use configured number of levels
        
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
            var numLevels = _config.NumberOfLevels; // Use configured number of levels
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
    /// Uses parallel execution for maximum performance (no rate limits on API)
    /// </summary>
    private async Task ExecuteReplacementsAsync(
        List<OrderReplacement> replacements,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        // Step 1: Cancel old orders in parallel (if they exist)
        var cancelsToProcess = replacements.Where(r => r.OldOrderId.HasValue).ToList();
        
        if (cancelsToProcess.Any())
        {
            _logger.LogInformation("Cancelling {Count} orders in parallel", cancelsToProcess.Count);
            
            var cancelTasks = cancelsToProcess.Select(async replacement =>
            {
                try
                {
                    _logger.LogDebug("Cancelling {Side} order at level {Level}: {OrderId}",
                        replacement.Side, replacement.LevelIndex, replacement.OldOrderId);
                    
                    var orderIdToCancel = replacement.OldOrderId!.Value;
                    await _orderService.CancelOrderAsync(
                        orderIdToCancel,
                        jwtToken,
                        cancellationToken);
                    
                    // Clear the level in state manager (thread-safe)
                    _stateManager.ClearLevel(replacement.Side, replacement.LevelIndex);
                    
                    _logger.LogInformation("✓ Cancelled {Side} order at level {Level}: {OrderId}",
                        replacement.Side, replacement.LevelIndex, replacement.OldOrderId);
                    
                    return (Success: true, Replacement: replacement);
                }
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError(httpEx, "✗ HTTP error cancelling {Side} order {OrderId} at level {Level} - API returned error, continuing",
                        replacement.Side, replacement.OldOrderId, replacement.LevelIndex);
                    
                    return (Success: false, Replacement: replacement);
                }
                catch (TaskCanceledException)
                {
                    _logger.LogWarning("✗ Timeout cancelling {Side} order {OrderId} at level {Level} - request cancelled, continuing",
                        replacement.Side, replacement.OldOrderId, replacement.LevelIndex);
                    
                    return (Success: false, Replacement: replacement);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "✗ Unexpected error cancelling {Side} order {OrderId} at level {Level}, continuing",
                        replacement.Side, replacement.OldOrderId, replacement.LevelIndex);
                    
                    return (Success: false, Replacement: replacement);
                }
            });
            
            var cancelResults = await Task.WhenAll(cancelTasks);
            var successfulCancels = cancelResults.Count(r => r.Success);
            var failedCancels = cancelResults.Count(r => !r.Success);
            
            _logger.LogInformation("Cancel batch complete: {Success} succeeded, {Failed} failed",
                successfulCancels, failedCancels);
        }
        
        // Step 2: Submit new orders in parallel
        if (replacements.Any())
        {
            _logger.LogInformation("Submitting {Count} orders in parallel", replacements.Count);
            
            var submitTasks = replacements.Select(async replacement =>
            {
                try
                {
                    _logger.LogDebug("Submitting {Side} order at level {Level}: Price={Price}, Qty={Quantity}",
                        replacement.Side, replacement.LevelIndex, replacement.NewPrice, replacement.NewQuantity);
                    
                    var response = await _orderService.SubmitLimitOrderAsync(
                        side: replacement.Side,
                        price: replacement.NewPrice,
                        quantity: replacement.NewQuantity,
                        marginFactor: (ulong)(_config.InitialMarginFactor * 1_000_000),
                        clientOrderId: $"MM-{replacement.Side}-L{replacement.LevelIndex}-{DateTime.UtcNow.Ticks}",
                        jwtToken: jwtToken,
                        cancellationToken: cancellationToken);
                    
                    // Update state manager with new order (thread-safe)
                    _stateManager.UpdateLevel(
                        replacement.Side,
                        replacement.LevelIndex,
                        response.OrderId,
                        replacement.NewPrice,
                        replacement.NewQuantity);
                    
                    _logger.LogInformation("✓ Placed {Side} order at level {Level}: OrderId={OrderId}, Status={Status}",
                        replacement.Side, replacement.LevelIndex, response.OrderId, response.OrderStatus);
                    
                    return (Success: true, Replacement: replacement, OrderId: response.OrderId);
                }
                catch (HttpRequestException httpEx)
                {
                    _logger.LogError(httpEx, "✗ HTTP error submitting {Side} order at level {Level} - API returned error, continuing",
                        replacement.Side, replacement.LevelIndex);
                    
                    return (Success: false, Replacement: replacement, OrderId: Guid.Empty);
                }
                catch (TaskCanceledException)
                {
                    _logger.LogWarning("✗ Timeout submitting {Side} order at level {Level} - request cancelled, continuing",
                        replacement.Side, replacement.LevelIndex);
                    
                    return (Success: false, Replacement: replacement, OrderId: Guid.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "✗ Unexpected error submitting {Side} order at level {Level}, continuing",
                        replacement.Side, replacement.LevelIndex);
                    
                    return (Success: false, Replacement: replacement, OrderId: Guid.Empty);
                }
            });
            
            var submitResults = await Task.WhenAll(submitTasks);
            var successfulSubmits = submitResults.Count(r => r.Success);
            var failedSubmits = submitResults.Count(r => !r.Success);
            
            _logger.LogInformation("Submit batch complete: {Success} succeeded, {Failed} failed",
                successfulSubmits, failedSubmits);
            
            if (failedSubmits > 0)
            {
                _logger.LogWarning("Market maker running with partial ladder: {ActiveOrders}/{TotalOrders} orders active",
                    successfulSubmits, replacements.Count);
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
            var bidPrices = PriceCalculator.CalculateBidLevelsUsd(
                midPriceBase, _config.BaseSpreadUsd, _config.LevelSpacingUsd, numLevels, _config.TradingDecimals);
            var askPrices = PriceCalculator.CalculateAskLevelsUsd(
                midPriceBase, _config.BaseSpreadUsd, _config.LevelSpacingUsd, numLevels, _config.TradingDecimals);
            
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

