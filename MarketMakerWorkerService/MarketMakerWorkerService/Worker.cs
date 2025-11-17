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
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<Worker> _logger;
    private IDisposable? _priceSubscription;

    public Worker(
        RedisIndexWatcher redisWatcher,
        BasicMarketMakerStrategy strategy,
        IMarketDataService marketDataService,
        IOptions<MarketMakerConfiguration> config,
        ILogger<Worker> logger)
    {
        _redisWatcher = redisWatcher;
        _strategy = strategy;
        _marketDataService = marketDataService;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("🚀 Market Maker Worker Service Starting");
        _logger.LogInformation("═══════════════════════════════════════════════════════════");
        _logger.LogInformation("📊 Configuration:");
        _logger.LogInformation("   Account: {AccountId}", _config.AccountId);
        _logger.LogInformation("   Trading Pair: BTC/USD");
        _logger.LogInformation("   Base Spread: {Spread} bps", _config.BaseSpreadBps);
        _logger.LogInformation("   Level Spacing: {Spacing} bps", _config.LevelSpacingBps);
        _logger.LogInformation("   Margin Factor: {Margin:P2}", _config.InitialMarginFactor);
        _logger.LogInformation("   Redis Index Key: {Key}", _config.RedisIndexKey);
        _logger.LogInformation("   Poll Interval: {Interval}ms", _config.RedisPollIntervalMs);
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        try
        {
            // Startup validation
            _logger.LogInformation("🔍 Performing startup validation...");
            
            // Fetch and validate market info
            var marketInfo = await _marketDataService.GetMarketInfoAsync(stoppingToken);
            _logger.LogInformation("✓ Market Info: Chain={ChainId}, Pair={Pair}, Decimals={Trading}/{Settlement}",
                marketInfo.ChainId, marketInfo.TradingPair, 
                marketInfo.TradingDecimals, marketInfo.SettlementDecimals);

            // Initialize strategy
            _logger.LogInformation("🎯 Initializing market making strategy...");
            _strategy.Initialize();
            _logger.LogInformation("✓ Strategy initialized successfully");

            // Start price monitoring
            _logger.LogInformation("📡 Starting Redis price monitoring (READ-ONLY)...");
            _logger.LogWarning("🚨 REDIS READ-ONLY MODE: Connected to production index server");
            
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
            _logger.LogInformation("✅ Market Maker is now RUNNING");
            _logger.LogInformation("═══════════════════════════════════════════════════════════");

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
        _logger.LogInformation("🛑 Market Maker Worker Service Stopping");
        _logger.LogInformation("═══════════════════════════════════════════════════════════");

        try
        {
            // Dispose price subscription
            _priceSubscription?.Dispose();
            _logger.LogInformation("✓ Price monitoring stopped");

            // Emergency stop strategy (cancel all orders)
            _logger.LogInformation("🚨 Cancelling all active orders...");
            await _strategy.EmergencyStopAsync(cancellationToken);
            _logger.LogInformation("✓ All orders cancelled");

            _logger.LogInformation("═══════════════════════════════════════════════════════════");
            _logger.LogInformation("✅ Market Maker stopped cleanly");
            _logger.LogInformation("═══════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during shutdown");
        }

        await base.StopAsync(cancellationToken);
    }
}
