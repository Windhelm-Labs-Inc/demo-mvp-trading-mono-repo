using System;
using System.Threading;
using System.Threading.Tasks;
using DotNetEnv;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Tests.Helpers;

namespace MarketMakerWorkerService.Tests.E2E;

/// <summary>
/// Integration test that runs the market maker continuously against the live API
/// for a specified duration, allowing observation of real-time behavior.
/// </summary>
public class ContinuousRunIntegrationTest : IDisposable
{
    // ============================================================================
    // TEST CONFIGURATION - ADJUST THESE PARAMETERS
    // ============================================================================
    private const int RUN_DURATION_SECONDS = 30; // How long to run the market maker
    
    private const int NUMBER_OF_LEVELS = 1;      // Levels per side
    private const string LEVEL_0_QUANTITY = "0.001";    // contracts
    private const string LEVELS_1_2_QUANTITY = "0.001"; // contracts
    private const string LEVELS_3_9_QUANTITY = "0.001"; // contracts
    
    private const string BASE_SPREAD_USD = "10.00";     // $10 spread
    private const string LEVEL_SPACING_USD = "5.00";    // $5 between levels
    private const string INITIAL_MARGIN_FACTOR = "0.2"; // 20% margin = 5x leverage
    
    // ============================================================================
    
    private readonly ITestOutputHelper _output;
    private readonly ILogger<ContinuousRunIntegrationTest> _testLogger;
    private readonly ServiceProvider _serviceProvider;
    private readonly MarketMakerConfiguration _config;
    
    public ContinuousRunIntegrationTest(ITestOutputHelper output)
    {
        _output = output;
        
        // Load environment variables
        Env.Load();
        
        // Build configuration
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // API Configuration
                ["MarketMaker:ApiBaseUrl"] = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net",
                
                // Hedera Account (from environment)
                ["MarketMaker:AccountId"] = Environment.GetEnvironmentVariable("HEDERA_ACCOUNT_ID") ?? throw new InvalidOperationException("HEDERA_ACCOUNT_ID not set"),
                ["MarketMaker:PrivateKeyDerHex"] = Environment.GetEnvironmentVariable("HEDERA_PRIVATE_KEY_DER_HEX") ?? throw new InvalidOperationException("HEDERA_PRIVATE_KEY_DER_HEX not set"),
                ["MarketMaker:LedgerId"] = Environment.GetEnvironmentVariable("HEDERA_LEDGER_ID") ?? "testnet",
                ["MarketMaker:KeyType"] = "ed25519",
                
                // Market Making Parameters (from constants above)
                ["MarketMaker:NumberOfLevels"] = NUMBER_OF_LEVELS.ToString(),
                ["MarketMaker:Level0Quantity"] = LEVEL_0_QUANTITY,
                ["MarketMaker:Levels1To2Quantity"] = LEVELS_1_2_QUANTITY,
                ["MarketMaker:Levels3To9Quantity"] = LEVELS_3_9_QUANTITY,
                ["MarketMaker:BaseSpreadUsd"] = BASE_SPREAD_USD,
                ["MarketMaker:LevelSpacingUsd"] = LEVEL_SPACING_USD,
                ["MarketMaker:BaseSpreadBps"] = "98",  // Legacy
                ["MarketMaker:LevelSpacingBps"] = "10", // Legacy
                
                // Market Configuration
                ["MarketMaker:TradingDecimals"] = TestConfiguration.MarketInfo.TradingDecimals.ToString(),
                ["MarketMaker:SettlementDecimals"] = TestConfiguration.MarketInfo.SettlementDecimals.ToString(),
                ["MarketMaker:InitialMarginFactor"] = INITIAL_MARGIN_FACTOR,
                
                // Redis Configuration (PRODUCTION)
                ["MarketMaker:RedisConnectionString"] = TestConfiguration.Redis.ConnectionString,
                ["MarketMaker:RedisIndexKey"] = TestConfiguration.Redis.ProductionIndexKey,
                ["MarketMaker:RedisPollIntervalMs"] = "1000", // 1 second poll interval
                
                // Operational Configuration
                ["MarketMaker:RateLimitDelaySeconds"] = "0" // No delays for testing
            })!
            .Build();
        
        _config = configuration.GetSection("MarketMaker").Get<MarketMakerConfiguration>()
            ?? throw new InvalidOperationException("Failed to load configuration");
        
        // Setup logging
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("System.Net.Http", LogLevel.Warning);
        });
        
        _testLogger = loggerFactory.CreateLogger<ContinuousRunIntegrationTest>();
        
        // Build service provider
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging();
        services.AddSingleton(Options.Create(_config));
        
        // HTTP Client for API
        services.AddHttpClient("PerpetualsAPI", client =>
        {
            client.BaseAddress = new Uri(_config.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        
        // Register all services (interfaces where required by dependencies)
        services.AddSingleton<IRedisConnectionService, RedisConnectionService>();
        services.AddSingleton<RedisIndexWatcher>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IOrderService, OrderService>();
        services.AddSingleton<MarketDataService>();
        services.AddSingleton<OrderStateManager>();
        services.AddSingleton<BasicMarketMakerStrategy>();
        
        _serviceProvider = services.BuildServiceProvider();
    }
    
    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~RunFullCycleForNSeconds")]
    public async Task RunFullCycleForNSeconds()
    {
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  CONTINUOUS RUN INTEGRATION TEST - LIVE API");
        _testLogger.LogInformation("  Duration: {Seconds} seconds", RUN_DURATION_SECONDS);
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  API: {ApiUrl}", _config.ApiBaseUrl);
        _testLogger.LogInformation("  Account: {AccountId}", _config.AccountId);
        _testLogger.LogInformation("  Levels: {Levels} per side", _config.NumberOfLevels);
        _testLogger.LogInformation("  Spread: ${Spread} USD", _config.BaseSpreadUsd);
        _testLogger.LogInformation("  Spacing: ${Spacing} USD", _config.LevelSpacingUsd);
        _testLogger.LogInformation("  Margin Factor: {Factor} ({Pct:F0}% margin = {Leverage:F1}x leverage)", 
            _config.InitialMarginFactor,
            _config.InitialMarginFactor * 100,
            1.0m / (decimal)_config.InitialMarginFactor);
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("");
        
        // Get services
        var authService = _serviceProvider.GetRequiredService<IAuthenticationService>();
        var accountService = _serviceProvider.GetRequiredService<IAccountService>();
        var orderService = _serviceProvider.GetRequiredService<IOrderService>();
        var redisWatcher = _serviceProvider.GetRequiredService<RedisIndexWatcher>();
        var strategy = _serviceProvider.GetRequiredService<BasicMarketMakerStrategy>();
        var stateManager = _serviceProvider.GetRequiredService<OrderStateManager>();
        var marketDataService = _serviceProvider.GetRequiredService<MarketDataService>();
        
        // Statistics tracking
        int priceUpdatesReceived = 0;
        int ordersCancelled = 0;
        int ordersFilled = 0;
        decimal? firstPrice = null;
        decimal? lastPrice = null;
        
        // ====================================================================
        // STEP 1: Authenticate
        // ====================================================================
        _testLogger.LogInformation("STEP 1: Authenticating with API...");
        var token = await authService.AuthenticateAsync(CancellationToken.None);
        _testLogger.LogInformation("✓ Authentication successful");
        _testLogger.LogInformation("");
        
        // ====================================================================
        // STEP 2: Check Initial Balance
        // ====================================================================
        _testLogger.LogInformation("STEP 2: Checking initial balance...");
        var initialSnapshot = await accountService.GetAccountSnapshotAsync(token, CancellationToken.None);
        var initialBalanceUsd = initialSnapshot.Balance / 1_000_000m;
        var initialOrders = initialSnapshot.Orders.Length;
        var initialPositions = initialSnapshot.Positions.Length;
        
        _testLogger.LogInformation("  Balance: ${Balance:F2} USD", initialBalanceUsd);
        _testLogger.LogInformation("  Existing Orders: {OrderCount}", initialOrders);
        _testLogger.LogInformation("  Existing Positions: {PositionCount}", initialPositions);
        _testLogger.LogInformation("✓ Initial state recorded");
        _testLogger.LogInformation("");
        
        // ====================================================================
        // STEP 3: Initialize Strategy
        // ====================================================================
        _testLogger.LogInformation("STEP 3: Initializing market maker strategy...");
        strategy.Initialize();
        _testLogger.LogInformation("✓ Strategy initialized");
        _testLogger.LogInformation("");
        
        // ====================================================================
        // STEP 4: Start Market Making
        // ====================================================================
        _testLogger.LogInformation("STEP 4: Starting market maker for {Seconds} seconds...", RUN_DURATION_SECONDS);
        _testLogger.LogInformation("  Press Ctrl+C to stop early (if running interactively)");
        _testLogger.LogInformation("");
        
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(RUN_DURATION_SECONDS));
        var startTime = DateTime.UtcNow;
        
        // Subscribe to price updates
        var subscription = redisWatcher.CreatePriceObservable(_config.RedisIndexKey, 1000, cts.Token)
            .Subscribe(async priceUpdate =>
            {
                try
                {
                    priceUpdatesReceived++;
                    if (!firstPrice.HasValue) firstPrice = priceUpdate.Price;
                    lastPrice = priceUpdate.Price;
                    
                    var elapsed = DateTime.UtcNow - startTime;
                    _testLogger.LogInformation("[{Elapsed:mm\\:ss}] Price Update #{Count}: ${Price:F2}", 
                        elapsed, priceUpdatesReceived, priceUpdate.Price);
                    
                    // Process price update through strategy
                    await strategy.OnIndexPriceUpdateAsync(priceUpdate.Price, cts.Token);
                    
                    // Count active orders
                    var activeOrders = stateManager.GetAllActiveOrderIds();
                    _testLogger.LogInformation("  Active orders in ladder: {Count}", activeOrders.Length);
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    _testLogger.LogError(ex, "Error processing price update");
                }
            },
            onError: ex => _testLogger.LogError(ex, "Error in price subscription"),
            onCompleted: () => _testLogger.LogInformation("Price subscription completed"));
        
        try
        {
            // Wait for duration
            await Task.Delay(TimeSpan.FromSeconds(RUN_DURATION_SECONDS), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        finally
        {
            subscription.Dispose();
        }
        
        var actualDuration = DateTime.UtcNow - startTime;
        _testLogger.LogInformation("");
        _testLogger.LogInformation("✓ Market maker ran for {Duration:mm\\:ss}", actualDuration);
        _testLogger.LogInformation("");
        
        // ====================================================================
        // STEP 5: Check Results
        // ====================================================================
        _testLogger.LogInformation("STEP 5: Checking final state...");
        
        // Wait for operations to settle
        _testLogger.LogInformation("  Waiting 2 seconds for operations to settle...");
        await Task.Delay(TimeSpan.FromSeconds(2));
        
        var finalSnapshot = await accountService.GetAccountSnapshotAsync(token, CancellationToken.None);
        var finalBalanceUsd = finalSnapshot.Balance / 1_000_000m;
        var finalOrders = finalSnapshot.Orders.Length;
        var finalPositions = finalSnapshot.Positions.Length;
        
        _testLogger.LogInformation("  Final Balance: ${Balance:F2} USD", finalBalanceUsd);
        _testLogger.LogInformation("  Open Orders: {OrderCount}", finalOrders);
        _testLogger.LogInformation("  Open Positions: {PositionCount}", finalPositions);
        
        // Count fills (any new positions)
        ordersFilled = finalPositions - initialPositions;
        if (ordersFilled > 0)
        {
            _testLogger.LogInformation("  ✓ {Count} order(s) were filled!", ordersFilled);
        }
        _testLogger.LogInformation("");
        
        // ====================================================================
        // STEP 6: Cleanup - Cancel All Orders
        // ====================================================================
        _testLogger.LogInformation("STEP 6: Cleanup - Cancelling all open orders...");
        
        if (finalOrders > 0)
        {
            int successfulCancels = 0;
            int failedCancels = 0;
            
            foreach (var order in finalSnapshot.Orders)
            {
                try
                {
                    _testLogger.LogInformation("  Cancelling order {OrderId}...", order.OrderId);
                    await orderService.CancelOrderAsync(order.OrderId, token, CancellationToken.None);
                    successfulCancels++;
                    ordersCancelled++;
                    _testLogger.LogInformation("    ✓ Cancelled");
                }
                catch (Exception ex)
                {
                    failedCancels++;
                    _testLogger.LogError(ex, "    ✗ Failed to cancel order {OrderId}", order.OrderId);
                }
            }
            
            _testLogger.LogInformation("  Cancellation complete: {Success} succeeded, {Failed} failed", 
                successfulCancels, failedCancels);
        }
        else
        {
            _testLogger.LogInformation("  No open orders to cancel");
        }
        _testLogger.LogInformation("✓ Cleanup complete");
        _testLogger.LogInformation("");
        
        // ====================================================================
        // SUMMARY
        // ====================================================================
        var balanceChange = finalBalanceUsd - initialBalanceUsd;
        var pnlString = balanceChange >= 0 ? $"+${balanceChange:F2}" : $"-${Math.Abs(balanceChange):F2}";
        
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  CONTINUOUS RUN TEST COMPLETE");
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  Run Duration: {Duration:mm\\:ss} ({Seconds} seconds)", actualDuration, (int)actualDuration.TotalSeconds);
        _testLogger.LogInformation("");
        _testLogger.LogInformation("  Price Updates Received: {Count}", priceUpdatesReceived);
        _testLogger.LogInformation("  First Price: ${Price:F2}", firstPrice ?? 0);
        _testLogger.LogInformation("  Last Price: ${Price:F2}", lastPrice ?? 0);
        _testLogger.LogInformation("  Price Change: ${Change:F2} ({Pct:F2}%)", 
            (lastPrice ?? 0) - (firstPrice ?? 0),
            firstPrice.HasValue && firstPrice.Value > 0 
                ? ((lastPrice ?? 0) - firstPrice.Value) / firstPrice.Value * 100 
                : 0);
        _testLogger.LogInformation("");
        _testLogger.LogInformation("  Orders Placed: ~{Count} (estimated from updates)", priceUpdatesReceived * _config.NumberOfLevels * 2);
        _testLogger.LogInformation("  Orders Filled: {Count}", ordersFilled);
        _testLogger.LogInformation("  Orders Cancelled: {Count}", ordersCancelled);
        _testLogger.LogInformation("");
        _testLogger.LogInformation("  Starting Balance: ${Balance:F2} USD", initialBalanceUsd);
        _testLogger.LogInformation("  Final Balance: ${Balance:F2} USD", finalBalanceUsd);
        _testLogger.LogInformation("  P&L: {PnL} USD", pnlString);
        _testLogger.LogInformation("================================================================================");
        
        // Assertions
        priceUpdatesReceived.Should().BeGreaterThan(0, "Should have received at least one price update");
        _testLogger.LogInformation("");
        _testLogger.LogInformation("✓ Test completed successfully");
    }
    
    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}

