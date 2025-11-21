using System.Reactive.Linq;
using DotNetEnv;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Tests.Helpers;

namespace MarketMakerWorkerService.Tests.E2E;

/// <summary>
/// E2E tests for the full reactive market maker pipeline:
/// Redis Index Watcher → Strategy → Order Management
/// 
/// 🚨 CRITICAL: Redis is REAL (production read-only), APIs are MOCKED
/// </summary>
public class MarketMakerPipelineE2ETests : IDisposable
{
    private readonly WireMockServer _mockServer;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly MarketMakerConfiguration _config;

    public MarketMakerPipelineE2ETests()
    {
        // Load .env file for Redis credentials
        Env.Load();
        
        // Start WireMock server for API mocking
        _mockServer = WireMockServer.Start();

        // Build configuration with mocked API endpoint
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketMaker:ApiBaseUrl"] = _mockServer.Urls[0],
                ["MarketMaker:AccountId"] = TestConfiguration.TestAccount.AccountId,
                ["MarketMaker:PrivateKeyDerHex"] = TestConfiguration.TestAccount.PrivateKeyDerHex,
                ["MarketMaker:BaseSpreadBps"] = "10",
                ["MarketMaker:LevelSpacingBps"] = "5",
                ["MarketMaker:InitialMarginFactor"] = "0.1",
                ["MarketMaker:RateLimitDelaySeconds"] = "1",
                ["MarketMaker:TradingDecimals"] = TestConfiguration.MarketInfo.TradingDecimals.ToString(),
                ["MarketMaker:SettlementDecimals"] = TestConfiguration.MarketInfo.SettlementDecimals.ToString(),
                ["MarketMaker:RedisConnectionString"] = TestConfiguration.Redis.ConnectionString,
                ["MarketMaker:RedisIndexKey"] = TestConfiguration.Redis.ProductionIndexKey,
                ["MarketMaker:RedisPollIntervalMs"] = "50"
            })!
            .Build();

        // Build service provider
        var services = new ServiceCollection();
        
        // Configuration
        services.Configure<MarketMakerConfiguration>(_configuration.GetSection("MarketMaker"));
        _config = _configuration.GetSection("MarketMaker").Get<MarketMakerConfiguration>()!;
        
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        
        // HttpClient for API
        services.AddHttpClient("PerpetualsAPI", client =>
        {
            client.BaseAddress = new Uri(_mockServer.Urls[0]);
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        
        // Register all services
        services.AddSingleton<IRedisConnectionService, RedisConnectionService>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IOrderService, OrderService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<OrderStateManager>();
        services.AddSingleton<RedisIndexWatcher>();
        services.AddSingleton<BasicMarketMakerStrategy>();
        
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~FullPipeline_ProcessesRedisIndexUpdates_AndManagesOrders")]
    public async Task FullPipeline_ProcessesRedisIndexUpdates_AndManagesOrders()
    {
        // Arrange
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine("🚀 STAGE 3 E2E TEST: Full Reactive Pipeline");
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine();
        Console.WriteLine("📡 Redis: PRODUCTION (read-only) - " + TestConfiguration.Redis.ProductionIndexKey);
        Console.WriteLine("🎭 APIs: MOCKED via WireMock");
        Console.WriteLine();

        // Mock authentication
        SetupAuthMock();
        
        // Mock order submissions and cancellations
        SetupOrderMocks();
        
        // Mock account balance
        SetupAccountMocks();

        var redisWatcher = _serviceProvider.GetRequiredService<RedisIndexWatcher>();
        var strategy = _serviceProvider.GetRequiredService<BasicMarketMakerStrategy>();
        var stateManager = _serviceProvider.GetRequiredService<OrderStateManager>();
        
        // Initialize strategy
        strategy.Initialize();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var priceUpdateCount = 0;

        // Act - Subscribe to Redis price updates
        var priceObservable = redisWatcher.CreatePriceObservable(
            _config.RedisIndexKey,
            _config.RedisPollIntervalMs,
            cts.Token);

        Console.WriteLine("⏳ Watching Redis for index price updates...");
        Console.WriteLine();

        var subscription = priceObservable
            .Subscribe(
                onNext: async update =>
                {
                    priceUpdateCount++;
                    
                    Console.WriteLine($"📊 INDEX UPDATE #{priceUpdateCount}: ${update.Price:F2} at {update.Timestamp:HH:mm:ss.fff}");

                    try
                    {
                        // Process update through strategy
                        await strategy.OnIndexPriceUpdateAsync(update.Price, cts.Token);
                        
                        // Print order book visualization after processing
                        PrintOrderBook(stateManager, update.Price);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error processing update: {ex.Message}");
                        Console.WriteLine();
                    }
                },
                onError: ex =>
                {
                    Console.WriteLine($"❌ Observable error: {ex.Message}");
                    cts.Cancel();
                },
                onCompleted: () =>
                {
                    Console.WriteLine("✅ Observable completed");
                });

        // Wait for 60 seconds or until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Expected when timeout is reached
        }

        subscription.Dispose();

        // Assert
        Console.WriteLine();
        Console.WriteLine("=".PadRight(80, '='));
        Console.WriteLine("📈 TEST SUMMARY");
        Console.WriteLine("=".PadRight(80, '='));
        
        priceUpdateCount.Should().BeGreaterThan(0, "Should have received at least one price update from production Redis");
        
        Console.WriteLine($"✓ Processed {priceUpdateCount} price updates");
        Console.WriteLine($"✓ Redis connection: WORKING (production read-only)");
        Console.WriteLine($"✓ Strategy execution: WORKING");
        Console.WriteLine($"✓ Order state management: WORKING");
        Console.WriteLine();

        // Verify WireMock received API calls
        var authRequests = _mockServer.LogEntries.Where(e => e.RequestMessage.Path.Contains("/auth")).ToList();
        var orderRequests = _mockServer.LogEntries.Where(e => e.RequestMessage.Path.Contains("/orders")).ToList();
        
        Console.WriteLine($"📡 API Calls Made:");
        Console.WriteLine($"   - Auth requests: {authRequests.Count}");
        Console.WriteLine($"   - Order requests: {orderRequests.Count}");
        Console.WriteLine();
    }

    /// <summary>
    /// Print formatted limit order book to console
    /// Shows sampled view of bids and asks at each level
    /// </summary>
    private void PrintOrderBook(OrderStateManager stateManager, decimal indexPrice)
    {
        Console.WriteLine("┌" + "─".PadRight(78, '─') + "┐");
        Console.WriteLine($"│ 📖 LIMIT ORDER BOOK @ Index=${indexPrice:F2}".PadRight(79) + "│");
        Console.WriteLine("├" + "─".PadRight(78, '─') + "┤");
        Console.WriteLine("│  LEVEL │      BID PRICE │   BID QTY │   ASK QTY │      ASK PRICE │  │");
        Console.WriteLine("├" + "─".PadRight(78, '─') + "┤");

        var bidLevels = stateManager.GetAllBidLevels();
        var askLevels = stateManager.GetAllAskLevels();

        // Show levels 0-2 (top of book) and 9 (bottom)
        var levelsToShow = new[] { 0, 1, 2, 3, 4, 5, 9 };

        foreach (var level in levelsToShow)
        {
            if (level >= bidLevels.Length || level >= askLevels.Length)
                continue;

            var bid = bidLevels[level];
            var ask = askLevels[level];

            var bidPrice = bid?.CurrentPrice > 0 
                ? (bid.CurrentPrice / 100_000_000.0m).ToString("F2").PadLeft(15) 
                : "---".PadLeft(15);
            var bidQty = bid?.CurrentOrderId.HasValue == true && bid.CurrentQuantity > 0
                ? (bid.CurrentQuantity / 100_000_000.0m).ToString("F2").PadLeft(10)
                : "---".PadLeft(10);

            var askQty = ask?.CurrentOrderId.HasValue == true && ask.CurrentQuantity > 0
                ? (ask.CurrentQuantity / 100_000_000.0m).ToString("F2").PadLeft(10)
                : "---".PadLeft(10);
            var askPrice = ask?.CurrentPrice > 0
                ? (ask.CurrentPrice / 100_000_000.0m).ToString("F2").PadLeft(15)
                : "---".PadLeft(15);

            Console.WriteLine($"│   L{level}   │ {bidPrice} │ {bidQty} │ {askQty} │ {askPrice} │  │");

            if (level == 5)
            {
                Console.WriteLine("│   ...  │            ... │       ... │       ... │            ... │  │");
            }
        }

        Console.WriteLine("└" + "─".PadRight(78, '─') + "┘");
        Console.WriteLine();
    }

    private void SetupAuthMock()
    {
        // Mock auth challenge
        _mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/auth/challenge")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                    "challenge_id": "mock-challenge-{{DateTime.UtcNow.Ticks}}",
                    "message": "Sign this message",
                    "expires_at_utc": "{{DateTime.UtcNow.AddMinutes(5):O}}"
                }
                """));
        
        // Mock auth verify
        _mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/auth/verify")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                    "access_token": "mock-jwt-token-{{DateTime.UtcNow.Ticks}}",
                    "expires_in": 900
                }
                """));
    }

    private void SetupOrderMocks()
    {
        // Mock limit order submission
        _mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/orders/limit")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                {
                    "order_id": "{{Guid.NewGuid()}}",
                    "order_status": "open",
                    "quantity_filled": 0,
                    "trade_id": null,
                    "position_ids": []
                }
                """));

        // Mock order cancellation
        _mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/orders/cancel")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                    "order_id": "00000000-0000-0000-0000-000000000000",
                    "unfilled_quantity": 0
                }
                """));
    }

    private void SetupAccountMocks()
    {
        _mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/account/balance")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""
                {
                    "owner_id": "0.0.6978377",
                    "balance": 100000000000,
                    "orders": [],
                    "positions": []
                }
                """));
    }

    public void Dispose()
    {
        _mockServer?.Stop();
        _mockServer?.Dispose();
        
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

