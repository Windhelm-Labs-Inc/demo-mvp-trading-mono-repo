using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Services;

namespace MarketMakerWorkerService;

/// <summary>
/// Main background worker service that orchestrates the market maker
/// Connects Redis price updates → Strategy → Order management
/// </summary>
public class Worker : BackgroundService
{
    private readonly RedisIndexWatcher _redisWatcher;
    private readonly BasicMarketMakerStrategy _strategy;
    private readonly IMarketDataService _marketDataService;
    private readonly IAuthenticationService _authService;
    private readonly IContinuousSettlementService? _settlementService;
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<Worker> _logger;
    private IDisposable? _priceSubscription;
    private Task? _tokenRefreshTask;

    public Worker(
        RedisIndexWatcher redisWatcher,
        BasicMarketMakerStrategy strategy,
        IMarketDataService marketDataService,
        IAuthenticationService authService,
        IContinuousSettlementService? settlementService,
        IOptions<MarketMakerConfiguration> config,
        ILogger<Worker> logger)
    {
        _redisWatcher = redisWatcher;
        _strategy = strategy;
        _marketDataService = marketDataService;
        _authService = authService;
        _settlementService = settlementService;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("Market Maker Worker Service Starting");
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("Configuration:");
        _logger.LogInformation("   Account: {AccountId}", _config.AccountId);
        _logger.LogInformation("   Trading Pair: BTC/USD");
        _logger.LogInformation("   Strategy: Fixed Dollar Amount");
        _logger.LogInformation("   Base Spread: ${SpreadUsd:F2} USD", _config.BaseSpreadUsd);
        _logger.LogInformation("   Level Spacing: ${SpacingUsd:F2} USD", _config.LevelSpacingUsd);
        _logger.LogInformation("   Number of Levels: {Levels} per side", _config.NumberOfLevels);
        _logger.LogInformation("   Margin Factor: {Margin:P2} ({Leverage:F1}x leverage)", 
            _config.InitialMarginFactor, 1.0m / _config.InitialMarginFactor);
        _logger.LogInformation("   Redis Index Key: {Key}", _config.RedisIndexKey);
        _logger.LogInformation("   Poll Interval: {Interval}ms", _config.RedisPollIntervalMs);
        
        if (_config.ContinuousSettlement == 1)
        {
            _logger.LogInformation("   Continuous Settlement: ENABLED");
            _logger.LogInformation("   Settlement Triggers: Startup, Shutdown, Token Refresh (~{Interval}s)", 
                _config.TokenRefreshIntervalSeconds);
        }
        
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        try
        {
            // Startup validation
            _logger.LogInformation("Performing startup validation...");
            
            // Fetch and validate market info
            var marketInfo = await _marketDataService.GetMarketInfoAsync(stoppingToken);
            _logger.LogInformation("Market Info: Chain={ChainId}, Pair={Pair}, Decimals={Trading}/{Settlement}",
                marketInfo.ChainId, marketInfo.TradingPair, 
                marketInfo.TradingDecimals, marketInfo.SettlementDecimals);

            // Initialize strategy
            _logger.LogInformation("Initializing market making strategy...");
            _strategy.Initialize();
            _logger.LogInformation("Strategy initialized successfully");

            // Run settlement check on startup if enabled
            if (_config.ContinuousSettlement == 1 && _settlementService != null)
            {
                _logger.LogInformation("═══════════════════════════════════════════════════════════");
                _logger.LogInformation("Running STARTUP settlement check...");
                _logger.LogInformation("═══════════════════════════════════════════════════════════");
                
                try
                {
                    var token = await _authService.GetValidTokenAsync(stoppingToken);
                    await RunSettlementCheckAsync(token, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Startup settlement check failed - continuing with service start");
                    // Don't throw - settlement failure shouldn't prevent service from starting
                }
                
                _logger.LogInformation("Startup settlement check complete");
            }

            // Start price monitoring
            _logger.LogInformation("Starting Redis price monitoring (READ-ONLY)...");
            _logger.LogWarning("REDIS READ-ONLY MODE: Connected to production index server");
            
            var priceObservable = _redisWatcher.CreatePriceObservable(
                _config.RedisIndexKey,
                _config.RedisPollIntervalMs,
                stoppingToken);

            // Subscribe to price updates
            _priceSubscription = priceObservable.Subscribe(
                onNext: async update =>
                {
                    try
                    {
                        _logger.LogDebug("Price update: ${Price:F2}", update.Price);
                        await _strategy.OnIndexPriceUpdateAsync(update.Price, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected during shutdown - don't log as error
                        _logger.LogDebug("Price update cancelled during shutdown");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing price update");
                    }
                },
                onError: ex =>
                {
                    _logger.LogCritical(ex, "Fatal error in price monitoring - initiating shutdown");
                },
                onCompleted: () =>
                {
                    _logger.LogInformation("Price monitoring completed");
                });

            _logger.LogInformation("═══════════════════════════════════════════════════════════");
            _logger.LogInformation("Market Maker is now RUNNING");
            _logger.LogInformation("═══════════════════════════════════════════════════════════");

            // Start background token refresh if configured
            if (_config.TokenRefreshIntervalSeconds > 0)
            {
                _logger.LogInformation("Starting background token refresh (interval: {Interval} seconds)", 
                    _config.TokenRefreshIntervalSeconds);
                _tokenRefreshTask = RunTokenRefreshLoopAsync(stoppingToken);
            }
            else
            {
                _logger.LogWarning("Background token refresh is DISABLED (TokenRefreshIntervalSeconds = 0)");
            }

            // Wait for cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Market Maker service is stopping (cancellation requested)");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in Market Maker service");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("Market Maker Worker Service Stopping");
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        try
        {
            // Step 1: Dispose price subscription first to stop new updates
            _priceSubscription?.Dispose();
            _logger.LogInformation("Price monitoring stopped");
            
            // Step 2: Give a small grace period for any in-flight price updates to complete
            await Task.Delay(50, CancellationToken.None);

            // Step 3: Emergency stop strategy (cancel all orders FIRST)
            _logger.LogInformation("Cancelling all active orders...");
            await _strategy.EmergencyStopAsync(cancellationToken);
            _logger.LogInformation("All orders cancelled");

            // Step 4: Run settlement check AFTER orders are cancelled
            if (_config.ContinuousSettlement == 1 && _settlementService != null)
            {
                _logger.LogInformation("═══════════════════════════════════════════════════════════");
                _logger.LogInformation("Running SHUTDOWN settlement check...");
                _logger.LogInformation("═══════════════════════════════════════════════════════════");
                
                try
                {
                    // Use a fresh cancellation token with timeout for shutdown settlement
                    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var token = await _authService.GetValidTokenAsync(shutdownCts.Token);
                    await RunSettlementCheckAsync(token, shutdownCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Shutdown settlement cancelled (timeout or service stopping)");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Shutdown settlement check failed - continuing with shutdown");
                    // Don't throw - settlement failure shouldn't prevent clean shutdown
                }
                
                _logger.LogInformation("Shutdown settlement check complete");
            }

            _logger.LogInformation("═══════════════════════════════════════════════════════════");
            _logger.LogInformation("Market Maker stopped cleanly");
            _logger.LogInformation("═══════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during shutdown");
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Background task that refreshes the authentication token periodically
    /// Runs independently without blocking the main execution loop
    /// </summary>
    private async Task RunTokenRefreshLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for the configured interval before first refresh
            await Task.Delay(TimeSpan.FromSeconds(_config.TokenRefreshIntervalSeconds), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Background token refresh triggered");
                    var token = await _authService.AuthenticateAsync(stoppingToken);
                    _logger.LogInformation("Token refreshed successfully (length: {Length} chars)", token.Length);
                    
                    // Run settlement check after token refresh if enabled
                    if (_config.ContinuousSettlement == 1 && _settlementService != null)
                    {
                        _logger.LogInformation("Running post-refresh settlement check...");
                        await RunSettlementCheckAsync(token, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Service is stopping, exit gracefully
                    _logger.LogInformation("Token refresh loop cancelled");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to refresh authentication token - will retry at next interval");
                }

                // Wait for next refresh interval
                await Task.Delay(TimeSpan.FromSeconds(_config.TokenRefreshIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Service is stopping, exit gracefully
            _logger.LogInformation("Token refresh loop stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in token refresh loop");
        }
    }

    /// <summary>
    /// Checks for settleable positions and executes settlement if found
    /// Runs at startup, shutdown, and after each token refresh when enabled
    /// </summary>
    private async Task RunSettlementCheckAsync(string token, CancellationToken stoppingToken)
    {
        if (_settlementService == null)
        {
            _logger.LogWarning("Settlement service not registered");
            return;
        }
        
        try
        {
            _logger.LogInformation("Checking for settleable positions");
            
            var result = await _settlementService.CheckAndSettlePositionsAsync(
                token, stoppingToken);
            
            if (result.Success && result.SettlementId != null)
            {
                _logger.LogInformation(
                    "Settled {Qty} units across {Count} positions (Settlement ID: {SettlementId})",
                    result.QuantitySettled, result.PositionsSettled, result.SettlementId);
            }
            else if (result.Success)
            {
                _logger.LogInformation("No settlement needed: {Message}", result.ErrorMessage ?? "No positions");
            }
            else
            {
                _logger.LogWarning("Settlement failed: {Error}", result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Settlement check encountered an error");
            // Don't throw - settlement failure shouldn't break the calling context
        }
    }
}
