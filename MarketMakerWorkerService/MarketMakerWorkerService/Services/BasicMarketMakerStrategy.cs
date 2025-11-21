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
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("CONFIGURATION VALUES:");
        // _logger.LogInformation("  Spread: ${SpreadUsd:F2} USD (legacy: {SpreadBps} bps)",
        //     _config.BaseSpreadUsd, _config.BaseSpreadBps);
        // _logger.LogInformation("  Level Spacing: ${LevelSpacingUsd:F2} USD (legacy: {LevelSpacingBps} bps)",
        //     _config.LevelSpacingUsd, _config.LevelSpacingBps);
        _logger.LogInformation("  Number of Levels: {NumLevels} per side", numLevels);
        _logger.LogInformation("  Initial Margin Factor: {MarginFactor} ({Pct:F0}% = {Leverage:F1}x leverage)",
            _config.InitialMarginFactor, _config.InitialMarginFactor * 100, 1.0m / _config.InitialMarginFactor);
        _logger.LogInformation("  Trading Decimals: {TradingDecimals}", _config.TradingDecimals);
        _logger.LogInformation("  Settlement Decimals: {SettlementDecimals}", _config.SettlementDecimals);
        _logger.LogInformation("  Redis Poll Interval: {PollIntervalMs}ms", _config.RedisPollIntervalMs);
        _logger.LogInformation("  Update Behavior: {Flag} ({Mode})", 
            _config.UpdateBehaviorFlag, 
            _config.UpdateBehaviorFlag == 1 ? "ATOMIC - maintains liquidity" : "SEQUENTIAL - may create gaps");
        if (_config.UpdateBehaviorFlag == 1)
        {
            if (_config.AtomicReplacementDelayMs > 0)
            {
                _logger.LogInformation("  Atomic Replacement Delay: {DelayMs}ms", _config.AtomicReplacementDelayMs);
            }
            _logger.LogInformation("  Self-Trade Prevention: {Status}", 
                _config.EnableSelfTradePrevention == 1 ? "ENABLED" : "DISABLED");
            if (_config.EnableSelfTradePrevention == 1)
            {
                _logger.LogInformation("  Sequential Peel Delay: {DelayMs}ms", _config.SequentialPeelDelayMs);
            }
        }
        _logger.LogInformation("LIQUIDITY SHAPE:");
        _logger.LogInformation("  Level 0: {L0} quantity (base units)", PriceCalculator.ToBaseUnits(_liquidityShape.Level0Size, _config.TradingDecimals));
        _logger.LogInformation("  Levels 1-2: {L12} quantity each (base units)", PriceCalculator.ToBaseUnits(_liquidityShape.Level1_2Size, _config.TradingDecimals));
        _logger.LogInformation("  Levels 3-9: {L39} quantity each (base units)", PriceCalculator.ToBaseUnits(_liquidityShape.Level3_9Size, _config.TradingDecimals));
        _logger.LogInformation("  Total per side: {Total} quantity (base units)", PriceCalculator.ToBaseUnits(_liquidityShape.TotalSize, _config.TradingDecimals));
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        
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

        // Check cancellation before acquiring semaphore to avoid racing with shutdown
        cancellationToken.ThrowIfCancellationRequested();

        await _strategyLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogDebug("Processing index price update: ${Price:F2}", indexPrice);
            
            // Get authentication token
            var token = await _authService.GetValidTokenAsync(cancellationToken);
            
            // Check cancellation after async operation
            cancellationToken.ThrowIfCancellationRequested();
            
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
            
            // Check cancellation before executing replacements
            cancellationToken.ThrowIfCancellationRequested();
            
            if (replacements.Count == 0)
            {
                _logger.LogDebug("No order changes needed (price within tolerance)");
                return;
            }
            
            _logger.LogDebug("Need to replace {Count} orders", replacements.Count);
            
            // Execute order replacements
            await ExecuteReplacementsAsync(replacements, token, cancellationToken);
            
            _logger.LogDebug("Successfully processed price update: ${Price:F2}", indexPrice);
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
    /// Check if new orders would cross existing orders (self-trade risk)
    /// Returns tuple indicating which side(s) would self-trade
    /// </summary>
    private (bool BidsCross, bool AsksCross) DetectCrossing(List<OrderReplacement> replacements)
    {
        var currentBids = _stateManager.GetAllBidLevels();
        var currentAsks = _stateManager.GetAllAskLevels();
        
        // Get new bid and ask prices from replacements
        var newBids = replacements.Where(r => r.Side == ContractSide.Long).OrderByDescending(r => r.NewPrice).ToList();
        var newAsks = replacements.Where(r => r.Side == ContractSide.Short).OrderBy(r => r.NewPrice).ToList();
        
        bool bidsCross = false;
        bool asksCross = false;
        
        // Check if best new bid >= any current ask (would match immediately)
        if (newBids.Any())
        {
            var bestNewBid = newBids.First().NewPrice;
            foreach (var currentAsk in currentAsks)
            {
                if (currentAsk?.CurrentOrderId.HasValue == true && currentAsk.CurrentPrice > 0)
                {
                    if (bestNewBid >= currentAsk.CurrentPrice)
                    {
                        _logger.LogWarning("Bid crossing detected: New bid ${NewBid} >= Current ask ${CurrentAsk}",
                            PriceCalculator.FromBaseUnits(bestNewBid, _config.TradingDecimals),
                            PriceCalculator.FromBaseUnits(currentAsk.CurrentPrice, _config.TradingDecimals));
                        bidsCross = true;
                        break;
                    }
                }
            }
        }
        
        // Check if best new ask <= any current bid (would match immediately)
        if (newAsks.Any())
        {
            var bestNewAsk = newAsks.First().NewPrice;
            foreach (var currentBid in currentBids)
            {
                if (currentBid?.CurrentOrderId.HasValue == true && currentBid.CurrentPrice > 0)
                {
                    if (bestNewAsk <= currentBid.CurrentPrice)
                    {
                        _logger.LogWarning("Ask crossing detected: New ask ${NewAsk} <= Current bid ${CurrentBid}",
                            PriceCalculator.FromBaseUnits(bestNewAsk, _config.TradingDecimals),
                            PriceCalculator.FromBaseUnits(currentBid.CurrentPrice, _config.TradingDecimals));
                        asksCross = true;
                        break;
                    }
                }
            }
        }
        
        return (bidsCross, asksCross);
    }
    
    /// <summary>
    /// Execute sequential peeling for a specific side (level-by-level processing)
    /// Processes orders inside-to-outside (L0 to LN) to minimize self-trading
    /// </summary>
    private async Task SequentialPeelBySideAsync(
        List<OrderReplacement> sideReplacements,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        var levels = sideReplacements.GroupBy(r => r.LevelIndex).OrderBy(g => g.Key);
        
        foreach (var levelGroup in levels)
        {
            var levelReplacements = levelGroup.ToList();
            
            // Cancel old orders at this level
            var cancelsToProcess = levelReplacements.Where(r => r.OldOrderId.HasValue).ToList();
            if (cancelsToProcess.Any())
            {
                var cancelResults = await CancelOrderBatchAsync(cancelsToProcess, jwtToken, cancellationToken, isAtomicMode: false);
                var successCount = cancelResults.Count(r => r.Success);
                _logger.LogDebug("Level {Level}: Cancelled {Success}/{Total} orders",
                    levelGroup.Key, successCount, cancelsToProcess.Count);
            }
            
            // Wait between cancel and submit
            if (_config.SequentialPeelDelayMs > 0)
            {
                await Task.Delay(_config.SequentialPeelDelayMs, cancellationToken);
            }
            
            cancellationToken.ThrowIfCancellationRequested();
            
            // Submit new orders at this level
            await SubmitNewOrdersAsync(levelReplacements, jwtToken, cancellationToken);
            
            // Wait between levels
            if (_config.SequentialPeelDelayMs > 0)
            {
                await Task.Delay(_config.SequentialPeelDelayMs, cancellationToken);
            }
            
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
    
    /// <summary>
    /// Execute order replacements with configurable behavior
    /// UpdateBehaviorFlag=1: Atomic with side-aware self-trade prevention
    /// UpdateBehaviorFlag=0: Sequential (cancel first, creates gap)
    /// </summary>
    private async Task ExecuteReplacementsAsync(
        List<OrderReplacement> replacements,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        if (_config.UpdateBehaviorFlag == 1)
        {
            // Check for self-trade risk if prevention is enabled
            if (_config.EnableSelfTradePrevention == 1)
            {
                var (bidsCross, asksCross) = DetectCrossing(replacements);
                
                if (bidsCross || asksCross)
                {
                    _logger.LogWarning("Self-trade risk detected - applying side-aware sequential peeling");
                    
                    var bidReplacements = replacements.Where(r => r.Side == ContractSide.Long).ToList();
                    var askReplacements = replacements.Where(r => r.Side == ContractSide.Short).ToList();
                    
                    if (bidsCross && asksCross)
                    {
                        // Both sides cross - sequential peel both sides
                        _logger.LogDebug("Both sides crossing - sequential peel on BOTH sides");
                        await SequentialPeelBySideAsync(bidReplacements, jwtToken, cancellationToken);
                        await SequentialPeelBySideAsync(askReplacements, jwtToken, cancellationToken);
                    }
                    else if (bidsCross)
                    {
                        // Bids crossing asks - peel ASKS (victims), atomic for BIDS (aggressors)
                        _logger.LogDebug("Bids crossing - sequential peel ASKS (remove victims first), then atomic BIDS");
                        
                        // Sequential peel asks (the side being crossed - remove victims first)
                        await SequentialPeelBySideAsync(askReplacements, jwtToken, cancellationToken);
                        
                        // Atomic for bids (submit then cancel - now safe, no old asks to match)
                        if (bidReplacements.Any())
                        {
                            await SubmitNewOrdersAsync(bidReplacements, jwtToken, cancellationToken);
                            
                            if (_config.AtomicReplacementDelayMs > 0)
                            {
                                await Task.Delay(_config.AtomicReplacementDelayMs, cancellationToken);
                            }
                            
                            cancellationToken.ThrowIfCancellationRequested();
                            await CancelOldOrdersAsync(bidReplacements, jwtToken, cancellationToken, isAtomicMode: true);
                        }
                    }
                    else // asksCross
                    {
                        // Asks crossing bids - peel BIDS (victims), atomic for ASKS (aggressors)
                        _logger.LogDebug("Asks crossing - sequential peel BIDS (remove victims first), then atomic ASKS");
                        
                        // Sequential peel bids (the side being crossed - remove victims first)
                        await SequentialPeelBySideAsync(bidReplacements, jwtToken, cancellationToken);
                        
                        // Atomic for asks (submit then cancel - now safe, no old bids to match)
                        if (askReplacements.Any())
                        {
                            await SubmitNewOrdersAsync(askReplacements, jwtToken, cancellationToken);
                            
                            if (_config.AtomicReplacementDelayMs > 0)
                            {
                                await Task.Delay(_config.AtomicReplacementDelayMs, cancellationToken);
                            }
                            
                            cancellationToken.ThrowIfCancellationRequested();
                            await CancelOldOrdersAsync(askReplacements, jwtToken, cancellationToken, isAtomicMode: true);
                        }
                    }
                    
                    return;
                }
            }
            
            // Normal atomic mode - no crossing detected
            _logger.LogDebug("Using ATOMIC order replacement strategy (maintains liquidity)");
            
            // Submit new orders first
            await SubmitNewOrdersAsync(replacements, jwtToken, cancellationToken);
            
            // Wait for orderbook to process new orders before canceling old ones
            if (_config.AtomicReplacementDelayMs > 0)
            {
                _logger.LogDebug("Waiting {DelayMs}ms for orderbook to process new orders before canceling old ones", 
                    _config.AtomicReplacementDelayMs);
                await Task.Delay(_config.AtomicReplacementDelayMs, cancellationToken);
            }
            
            cancellationToken.ThrowIfCancellationRequested();
            
            // Then cancel old ones
            await CancelOldOrdersAsync(replacements, jwtToken, cancellationToken, isAtomicMode: true);
        }
        else
        {
            _logger.LogDebug("Using SEQUENTIAL order replacement strategy (may create gaps)");
            // Cancel old orders first, THEN submit new ones (original behavior)
            await CancelOldOrdersAsync(replacements, jwtToken, cancellationToken, isAtomicMode: false);
            cancellationToken.ThrowIfCancellationRequested();
            await SubmitNewOrdersAsync(replacements, jwtToken, cancellationToken);
        }
    }
    
    /// <summary>
    /// Cancel old orders in parallel with retry logic
    /// </summary>
    private async Task CancelOldOrdersAsync(
        List<OrderReplacement> replacements,
        string jwtToken,
        CancellationToken cancellationToken,
        bool isAtomicMode = false)
    {
        var cancelsToProcess = replacements.Where(r => r.OldOrderId.HasValue).ToList();
        
        if (!cancelsToProcess.Any())
        {
            _logger.LogDebug("No orders to cancel");
            return;
        }
        
        _logger.LogDebug("Cancelling {Count} orders in parallel", cancelsToProcess.Count);
        
        // First attempt
        var cancelResults = await CancelOrderBatchAsync(cancelsToProcess, jwtToken, cancellationToken, isAtomicMode);
        
        var successfulCancels = cancelResults.Count(r => r.Success);
        var failedCancels = cancelResults.Count(r => !r.Success);
        
        _logger.LogDebug("Cancel batch complete: {Success} succeeded, {Failed} failed",
            successfulCancels, failedCancels);
        
        // Retry failed cancellations once
        if (failedCancels > 0)
        {
            var failedReplacements = cancelResults.Where(r => !r.Success).Select(r => r.Replacement).ToList();
            
            _logger.LogDebug("Retrying {Count} failed cancellations after 50ms delay", failedReplacements.Count);
            
            try
            {
                await Task.Delay(50, cancellationToken);
                
                var retryResults = await CancelOrderBatchAsync(failedReplacements, jwtToken, cancellationToken, isAtomicMode);
                
                var retrySuccesses = retryResults.Count(r => r.Success);
                var retryFailures = retryResults.Count(r => !r.Success);
                
                _logger.LogDebug("Retry batch complete: {Success} succeeded, {Failed} failed",
                    retrySuccesses, retryFailures);
                
                if (retryFailures > 0)
                {
                    _logger.LogWarning("{Count} orders could not be cancelled after retry (likely already filled/closed)",
                        retryFailures);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Retry cancelled during shutdown");
            }
        }
    }
    
    /// <summary>
    /// Execute a batch of order cancellations in parallel
    /// </summary>
    private async Task<(bool Success, OrderReplacement Replacement)[]> CancelOrderBatchAsync(
        List<OrderReplacement> replacements,
        string jwtToken,
        CancellationToken cancellationToken,
        bool isAtomicMode)
    {
        var cancelTasks = replacements.Select(async replacement =>
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
                
                // Only clear level in sequential mode
                // In atomic mode, the level already has the new order ID
                if (!isAtomicMode)
                {
                    _stateManager.ClearLevel(replacement.Side, replacement.LevelIndex);
                }
                
                _logger.LogDebug("Cancelled {Side} order at level {Level}: {OrderId}",
                    replacement.Side, replacement.LevelIndex, replacement.OldOrderId);
                
                return (Success: true, Replacement: replacement);
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP error cancelling {Side} order {OrderId} at level {Level} - API returned error, continuing",
                    replacement.Side, replacement.OldOrderId, replacement.LevelIndex);
                
                return (Success: false, Replacement: replacement);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Cancelling {Side} order {OrderId} at level {Level} - operation cancelled during shutdown",
                    replacement.Side, replacement.OldOrderId, replacement.LevelIndex);
                
                return (Success: false, Replacement: replacement);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error cancelling {Side} order {OrderId} at level {Level}, continuing",
                    replacement.Side, replacement.OldOrderId, replacement.LevelIndex);
                
                return (Success: false, Replacement: replacement);
            }
        });
        
        return await Task.WhenAll(cancelTasks);
    }
    
    /// <summary>
    /// Submit new orders in parallel
    /// </summary>
    private async Task SubmitNewOrdersAsync(
        List<OrderReplacement> replacements,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        if (!replacements.Any())
        {
            _logger.LogDebug("No orders to submit");
            return;
        }
        
        _logger.LogDebug("Submitting {Count} orders in parallel", replacements.Count);
        
        var submitTasks = replacements.Select(async replacement =>
        {
            try
            {
                var marginFactorBase = (ulong)(_config.InitialMarginFactor * 1_000_000);
                var priceDecimal = PriceCalculator.FromBaseUnits(replacement.NewPrice, _config.TradingDecimals);
                var qtyDecimal = PriceCalculator.FromBaseUnits(replacement.NewQuantity, _config.TradingDecimals);
                var marginRequired = PriceCalculator.CalculateMargin(
                    replacement.NewPrice,
                    replacement.NewQuantity,
                    marginFactorBase,
                    _config.TradingDecimals,
                    _config.SettlementDecimals);
                var marginDecimal = PriceCalculator.FromBaseUnits(marginRequired, _config.SettlementDecimals);
                
                _logger.LogDebug("Submitting {Side} order at level {Level}: Price=${Price:F2}, Qty={Quantity:F8}, Margin=${Margin:F2}",
                    replacement.Side, replacement.LevelIndex, priceDecimal, qtyDecimal, marginDecimal);
                
                var response = await _orderService.SubmitLimitOrderAsync(
                    side: replacement.Side,
                    price: replacement.NewPrice,
                    quantity: replacement.NewQuantity,
                    marginFactor: marginFactorBase,
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
                
                _logger.LogDebug("Placed {Side} order at level {Level}: OrderId={OrderId}, Status={Status}",
                    replacement.Side, replacement.LevelIndex, response.OrderId, response.OrderStatus);
                
                return (Success: true, Replacement: replacement, OrderId: response.OrderId);
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "HTTP error submitting {Side} order at level {Level} - API returned error, continuing",
                    replacement.Side, replacement.LevelIndex);
                
                return (Success: false, Replacement: replacement, OrderId: Guid.Empty);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Submitting {Side} order at level {Level} - operation cancelled during shutdown",
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
        
        _logger.LogDebug("Submit batch complete: {Success} succeeded, {Failed} failed",
            successfulSubmits, failedSubmits);
        
        if (failedSubmits > 0)
        {
            _logger.LogWarning("Market maker running with partial ladder: {ActiveOrders}/{TotalOrders} orders active",
                successfulSubmits, replacements.Count);
        }
    }
    //
    // /// <summary>
    // /// Check account balance and ensure sufficient capital
    // /// </summary>
    // public async Task<bool> HasSufficientCapitalAsync(decimal indexPrice, CancellationToken cancellationToken)
    // {
    //     try
    //     {
    //         var numLevels = _config.NumberOfLevels;
    //         var token = await _authService.GetValidTokenAsync(cancellationToken);
    //         var snapshot = await _accountService.GetAccountSnapshotAsync(token, cancellationToken);
    //         
    //         var midPriceBase = PriceCalculator.ToBaseUnits(indexPrice, _config.TradingDecimals);
    //         var bidPrices = PriceCalculator.CalculateBidLevelsUsd(
    //             midPriceBase, _config.BaseSpreadUsd, _config.LevelSpacingUsd, numLevels, _config.TradingDecimals);
    //         var askPrices = PriceCalculator.CalculateAskLevelsUsd(
    //             midPriceBase, _config.BaseSpreadUsd, _config.LevelSpacingUsd, numLevels, _config.TradingDecimals);
    //         
    //         // Calculate quantities and required margin
    //         var quantities = LiquidityShapeCalculator.CalculateQuantities(_liquidityShape, _config.TradingDecimals, numLevels);
    //         var marginFactorBase = (ulong)(_config.InitialMarginFactor * 1_000_000);
    //         var totalMargin = PriceCalculator.CalculateTotalCapitalRequired(
    //             bidPrices,
    //             askPrices,
    //             quantities,
    //             marginFactorBase,
    //             _config.TradingDecimals,
    //             _config.SettlementDecimals);
    //         
    //         var maxUsableBalance = (ulong)(snapshot.Balance * _config.BalanceUtilization);
    //         var balanceDecimal = PriceCalculator.FromBaseUnits(snapshot.Balance, _config.SettlementDecimals);
    //         var usableDecimal = PriceCalculator.FromBaseUnits(maxUsableBalance, _config.SettlementDecimals);
    //         var requiredDecimal = PriceCalculator.FromBaseUnits(totalMargin, _config.SettlementDecimals);
    //         
    //         _logger.LogInformation(
    //             "Capital Check: Balance=${Balance:F2} (usable: ${Usable:F2} @ {Utilization:P0}), Required=${Required:F2}",
    //             balanceDecimal, usableDecimal, _config.BalanceUtilization, requiredDecimal);
    //         
    //         var hasSufficient = LiquidityShapeCalculator.HasSufficientCapital(
    //             _liquidityShape,
    //             bidPrices,
    //             askPrices,
    //             snapshot.Balance,
    //             marginFactorBase,
    //             _config.TradingDecimals,
    //             _config.SettlementDecimals,
    //             _config.BalanceUtilization);
    //         
    //         if (!hasSufficient)
    //         {
    //             _logger.LogWarning("INSUFFICIENT CAPITAL: Need ${Required:F2} but only have ${Usable:F2} available",
    //                 requiredDecimal, usableDecimal);
    //         }
    //         
    //         return hasSufficient;
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError(ex, "Error checking capital sufficiency");
    //         return false;
    //     }
    // }

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

