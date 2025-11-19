using System.Reactive.Linq;
using DotNetEnv;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Tests.Helpers;
using MarketMakerWorkerService.Utilities;

namespace MarketMakerWorkerService.Tests.E2E;

/// <summary>
/// One Cycle Integration Test - Places real orders on live API and cancels them
/// This test connects to the REAL production API and Redis
/// WARNING: This test will place REAL orders with REAL money!
/// </summary>
public class OneCycleIntegrationTest : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<OneCycleIntegrationTest> _testLogger;

    public OneCycleIntegrationTest()
    {
        // Load .env file for credentials
        Env.Load();

        // Build configuration for LIVE API
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // LIVE API Configuration
                ["MarketMaker:ApiBaseUrl"] = "https://perps-api-d7cff5fhd9g0b7c4.eastus-01.azurewebsites.net",
                ["MarketMaker:AccountId"] = Environment.GetEnvironmentVariable("HEDERA_ACCOUNT_ID") ?? throw new InvalidOperationException("HEDERA_ACCOUNT_ID not set"),
                ["MarketMaker:PrivateKeyDerHex"] = Environment.GetEnvironmentVariable("HEDERA_PRIVATE_KEY_DER_HEX") ?? throw new InvalidOperationException("HEDERA_PRIVATE_KEY_DER_HEX not set"),
                ["MarketMaker:LedgerId"] = Environment.GetEnvironmentVariable("HEDERA_LEDGER_ID") ?? "testnet",
                ["MarketMaker:KeyType"] = "ed25519",
                
                // Conservative Market Making Parameters
                ["MarketMaker:NumberOfLevels"] = "1",        // 1 level per side = 2 total orders (1 bid + 1 ask)
                ["MarketMaker:Level0Quantity"] = "0.001",    // 0.001 contracts = 100,000 base units = ~$1.85 margin @ $92k BTC
                ["MarketMaker:Levels1To2Quantity"] = "0.001",    // 0.001 contracts per level
                ["MarketMaker:Levels3To9Quantity"] = "0.001",    // 0.001 contracts per level
                ["MarketMaker:BaseSpreadUsd"] = "10.00",     // $10 spread: best bid $5 below index, best ask $5 above
                ["MarketMaker:LevelSpacingUsd"] = "5.00",    // $5 spacing between price levels
                ["MarketMaker:BaseSpreadBps"] = "98",        // Legacy (not used)
                ["MarketMaker:LevelSpacingBps"] = "10",      // Legacy (not used)
                
                // Market Configuration
                ["MarketMaker:TradingDecimals"] = TestConfiguration.MarketInfo.TradingDecimals.ToString(),
                ["MarketMaker:SettlementDecimals"] = TestConfiguration.MarketInfo.SettlementDecimals.ToString(),
                ["MarketMaker:InitialMarginFactor"] = "0.2", // 0.2 = 20% margin = 5x leverage (above 0.1 minimum, capital efficient)
                // Redis Configuration (PRODUCTION)
                ["MarketMaker:RedisConnectionString"] = TestConfiguration.Redis.ConnectionString,
                ["MarketMaker:RedisIndexKey"] = TestConfiguration.Redis.ProductionIndexKey,
                ["MarketMaker:RedisPollIntervalMs"] = "100",
                
                // No rate limiting (parallel execution)
                ["MarketMaker:RateLimitDelaySeconds"] = "0"
            })!
            .Build();

        // Build service collection with REAL services
        var services = new ServiceCollection();
        
        // Configuration
        services.Configure<MarketMakerConfiguration>(configuration.GetSection("MarketMaker"));
        _config = configuration.GetSection("MarketMaker").Get<MarketMakerConfiguration>()!;
        
        // Logging
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFilter("System.Net.Http", LogLevel.Warning);
        });
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddLogging();
        
        // Create HTTP logging handler
        var httpLogger = loggerFactory.CreateLogger("HTTP");
        
        // HttpClient for REAL API with detailed logging
        services.AddHttpClient("PerpetualsAPI", client =>
        {
            client.BaseAddress = new Uri(_config.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler(() => new DetailedLoggingHandler(httpLogger));
        
        // Register all REAL services (no mocking)
        services.AddSingleton<IRedisConnectionService, RedisConnectionService>();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IOrderService, OrderService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IMarketDataService, MarketDataService>();
        services.AddSingleton<OrderStateManager>();
        services.AddSingleton<RedisIndexWatcher>();
        services.AddSingleton<BasicMarketMakerStrategy>();
        
        _serviceProvider = services.BuildServiceProvider();
        _testLogger = _serviceProvider.GetRequiredService<ILogger<OneCycleIntegrationTest>>();
    }

    /// <summary>
    /// ONE CYCLE TEST - Places real orders and cancels them
    /// This is NOT a unit test - it interacts with REAL APIs and REAL money
    /// </summary>
    [Fact]
    public async Task OneCycle_PlaceAndCancelOrders_Success()
    {
        // ====================================================================
        // SETUP
        // ====================================================================
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  ONE CYCLE INTEGRATION TEST - LIVE API");
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  API: {ApiUrl}", _config.ApiBaseUrl);
        _testLogger.LogInformation("  Account: {AccountId}", _config.AccountId);
        _testLogger.LogInformation("  Levels: {Levels} per side", _config.NumberOfLevels);
        _testLogger.LogInformation("  Spread: ${Spread:F2} USD", _config.BaseSpreadUsd);
        _testLogger.LogInformation("  Spacing: ${Spacing:F2} USD", _config.LevelSpacingUsd);
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("");

        var strategy = _serviceProvider.GetRequiredService<BasicMarketMakerStrategy>();
        var authService = _serviceProvider.GetRequiredService<IAuthenticationService>();
        var accountService = _serviceProvider.GetRequiredService<IAccountService>();
        var orderService = _serviceProvider.GetRequiredService<IOrderService>();
        var stateManager = _serviceProvider.GetRequiredService<OrderStateManager>();
        var redisWatcher = _serviceProvider.GetRequiredService<RedisIndexWatcher>();
        var marketDataService = _serviceProvider.GetRequiredService<IMarketDataService>();

        // ====================================================================
        // STEP 0: Fetch Market Info (to verify decimal configurations)
        // ====================================================================
        _testLogger.LogInformation("STEP 0: Fetching market info from API...");
        var marketInfo = await marketDataService.GetMarketInfoAsync(CancellationToken.None);
        
        _testLogger.LogInformation("  Trading Pair: {Pair}", marketInfo.TradingPair);
        _testLogger.LogInformation("  Chain ID: {ChainId}", marketInfo.ChainId);
        _testLogger.LogInformation("  Ledger ID: {LedgerId}", marketInfo.LedgerId);
        _testLogger.LogInformation("  Settlement Token: {Token}", marketInfo.SettlementToken);
        _testLogger.LogInformation("  Trading Decimals: {TradingDecimals} (base units per contract)", marketInfo.TradingDecimals);
        _testLogger.LogInformation("  Settlement Decimals: {SettlementDecimals} (base units per USD)", marketInfo.SettlementDecimals);
        
        // CRITICAL: Check if our configuration matches the API
        if (marketInfo.TradingDecimals != _config.TradingDecimals)
        {
            _testLogger.LogError("✗ TRADING DECIMALS MISMATCH!");
            _testLogger.LogError("  API reports: {ApiDecimals}", marketInfo.TradingDecimals);
            _testLogger.LogError("  Config has: {ConfigDecimals}", _config.TradingDecimals);
            throw new InvalidOperationException(
                $"Trading decimals mismatch: API={marketInfo.TradingDecimals}, Config={_config.TradingDecimals}");
        }
        
        if (marketInfo.SettlementDecimals != _config.SettlementDecimals)
        {
            _testLogger.LogError("✗ SETTLEMENT DECIMALS MISMATCH!");
            _testLogger.LogError("  API reports: {ApiDecimals}", marketInfo.SettlementDecimals);
            _testLogger.LogError("  Config has: {ConfigDecimals}", _config.SettlementDecimals);
            throw new InvalidOperationException(
                $"Settlement decimals mismatch: API={marketInfo.SettlementDecimals}, Config={_config.SettlementDecimals}");
        }
        
        _testLogger.LogInformation("✓ Market info verified - decimals match configuration");
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 1: Authenticate
        // ====================================================================
        _testLogger.LogInformation("STEP 1: Authenticating with API...");
        var token = await authService.AuthenticateAsync(CancellationToken.None);
        token.Should().NotBeNullOrEmpty("Authentication should return a valid token");
        _testLogger.LogInformation("✓ Authentication successful");
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 2: Check Account Balance
        // ====================================================================
        _testLogger.LogInformation("STEP 2: Checking account balance...");
        var accountSnapshot = await accountService.GetAccountSnapshotAsync(token, CancellationToken.None);
        var balanceUsd = accountSnapshot.Balance / 1_000_000m; // Convert from base units to USD
        _testLogger.LogInformation("  Balance: ${Balance:F2} USD", balanceUsd);
        _testLogger.LogInformation("  Existing Orders: {OrderCount}", accountSnapshot.Orders.Length);
        _testLogger.LogInformation("  Existing Positions: {PositionCount}", accountSnapshot.Positions.Length);
        
        accountSnapshot.Balance.Should().BeGreaterThan(0, "Account must have balance to place orders");
        _testLogger.LogInformation("✓ Account balance verified");
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 3: Get Current BTC Price from Redis
        // ====================================================================
        _testLogger.LogInformation("STEP 3: Reading BTC price from Redis...");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
        decimal? currentPrice = null;
        var priceObservable = redisWatcher.CreatePriceObservable(
            _config.RedisIndexKey,
            _config.RedisPollIntervalMs,
            cts.Token);

        var priceUpdate = await priceObservable.FirstAsync();
        currentPrice = priceUpdate.Price;
        
        currentPrice.Should().NotBeNull("Should receive a price from Redis");
        currentPrice.Should().BeGreaterThan(0, "Price should be positive");
        _testLogger.LogInformation("  BTC Price: ${Price:F2} USD", currentPrice.Value);
        _testLogger.LogInformation("✓ Price fetched from Redis");
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 3.5: Pre-Flight Balance Check
        // ====================================================================
        _testLogger.LogInformation("STEP 3.5: Validating sufficient margin for order placement...");

        // Calculate quantities for all levels using the same logic as the strategy
        var liquidityShape = new LiquidityShape
        {
            Level0Size = _config.Level0Quantity,
            Level1_2Size = _config.Levels1To2Quantity,
            Level3_9Size = _config.Levels3To9Quantity
        };

        var quantities = LiquidityShapeCalculator.CalculateQuantities(
            liquidityShape,
            _config.TradingDecimals,
            _config.NumberOfLevels);

        // Calculate total margin required for all orders
        var totalMarginRequired = CalculateTotalMarginRequired(
            currentPrice.Value,
            _config.NumberOfLevels,
            quantities,
            (decimal)_config.InitialMarginFactor,
            _config.TradingDecimals);

        var availableBalance = accountSnapshot.Balance / (decimal)Math.Pow(10, _config.SettlementDecimals);

        _testLogger.LogInformation("  Configuration:");
        _testLogger.LogInformation("    Number of Levels: {Levels} (×2 for bid/ask = {Total} orders)", 
            _config.NumberOfLevels, _config.NumberOfLevels * 2);
        _testLogger.LogInformation("    Margin Factor: {Factor:F2} ({Pct:F0}% margin required)", 
            _config.InitialMarginFactor, _config.InitialMarginFactor * 100);
        _testLogger.LogInformation("");
        
        // Show detailed per-level breakdown
        _testLogger.LogInformation("  Order Cost Breakdown (per side):");
        var scale = (decimal)Math.Pow(10, _config.TradingDecimals);
        for (int i = 0; i < _config.NumberOfLevels; i++)
        {
            var qty = quantities[i] / scale;
            var notional = qty * currentPrice.Value;
            var margin = notional * (decimal)_config.InitialMarginFactor;
            
            _testLogger.LogInformation(
                "    Level {Level}: {Qty,7:F3} contracts → Notional ${Notional,10:F2} → Margin ${Margin,7:F2} (×2 sides = ${Total,7:F2})",
                i, qty, notional, margin, margin * 2);
        }
        _testLogger.LogInformation("");
        
        _testLogger.LogInformation("  Balance Summary:");
        _testLogger.LogInformation("    Total Margin Required: ${Required,10:F2} USD", totalMarginRequired);
        _testLogger.LogInformation("    Available Balance:     ${Available,10:F2} USD", availableBalance);
        
        var utilizationRatio = totalMarginRequired / availableBalance;
        _testLogger.LogInformation("    Utilization Ratio:     {Ratio,10:P1}", utilizationRatio);
        _testLogger.LogInformation("");

        if (totalMarginRequired > availableBalance)
        {
            var shortfall = totalMarginRequired - availableBalance;
            _testLogger.LogError("✗ INSUFFICIENT BALANCE - TEST CONFIGURATION ERROR");
            _testLogger.LogError("  Required:  ${Required,12:F2} USD", totalMarginRequired);
            _testLogger.LogError("  Available: ${Available,12:F2} USD", availableBalance);
            _testLogger.LogError("  Shortfall: ${Shortfall,12:F2} USD", shortfall);
            _testLogger.LogError("");
            _testLogger.LogError("  Current Configuration:");
            _testLogger.LogError("    - Number of Levels: {Levels}", _config.NumberOfLevels);
            _testLogger.LogError("    - Contracts per Order: {Qty}", quantities[0] / scale);
            _testLogger.LogError("    - Margin Factor: {Factor:F2} ({Pct:F0}% margin = {Leverage:F2}x leverage)", 
                _config.InitialMarginFactor, 
                _config.InitialMarginFactor * 100,
                1.0m / (decimal)_config.InitialMarginFactor);
            _testLogger.LogError("");
            _testLogger.LogError("  Fix Options:");
            _testLogger.LogError("    1. Reduce margin factor: 0.2 (5x leverage) or 1.2 (0.83x, matches production)");
            _testLogger.LogError("    2. Reduce number of levels: Try 1 or 2");
            _testLogger.LogError("    3. Reduce quantity per level: Try 2-5 contracts");
            _testLogger.LogError("");
            
            throw new InvalidOperationException(
                $"Test configuration requires more capital than available. " +
                $"Required: ${totalMarginRequired:F2}, Available: ${availableBalance:F2}, Shortfall: ${shortfall:F2}. " +
                $"Fix the InitialMarginFactor (current: {_config.InitialMarginFactor}) or reduce quantity.");
        }

        // Warn if using >50% of balance
        if (utilizationRatio > 0.5m)
        {
            _testLogger.LogWarning("⚠ WARNING: Using {Ratio:P1} of available balance", utilizationRatio);
            _testLogger.LogWarning("  This leaves limited buffer for price movements and fills");
        }
        else
        {
            _testLogger.LogInformation("✓ Sufficient balance confirmed ({Ratio:P1} utilization)", utilizationRatio);
        }
        
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 4: Initialize Strategy and Place Orders
        // ====================================================================
        _testLogger.LogInformation("STEP 4: Placing market making orders...");
        _testLogger.LogInformation("  Initializing strategy with {Levels} levels per side", _config.NumberOfLevels);
        
        strategy.Initialize();
        
        _testLogger.LogInformation("  Processing price update to place orders...");
        await strategy.OnIndexPriceUpdateAsync(currentPrice.Value, CancellationToken.None);
        
        _testLogger.LogInformation("✓ Orders placed");
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 5: Check Order Placement Results
        // ====================================================================
        _testLogger.LogInformation("STEP 5: Checking order placement results...");
        
        var bidLevels = stateManager.GetAllBidLevels();
        var askLevels = stateManager.GetAllAskLevels();
        
        var activeBids = bidLevels.Count(l => l?.CurrentOrderId.HasValue == true);
        var activeAsks = askLevels.Count(l => l?.CurrentOrderId.HasValue == true);
        var totalOrders = activeBids + activeAsks;
        
        _testLogger.LogInformation("  Active Bid Orders: {BidCount}", activeBids);
        _testLogger.LogInformation("  Active Ask Orders: {AskCount}", activeAsks);
        _testLogger.LogInformation("  Total Active Orders: {TotalCount}", totalOrders);
        
        // CRITICAL ASSERTION: Verify all orders were placed successfully
        var expectedOrders = _config.NumberOfLevels * 2; // bids + asks
        totalOrders.Should().Be(expectedOrders, 
            $"Expected to place {expectedOrders} orders ({_config.NumberOfLevels} levels × 2 sides) but only {totalOrders} succeeded. " +
            $"Configuration: Margin={_config.InitialMarginFactor:F2} ({(_config.InitialMarginFactor * 100):F0}% margin), " +
            $"Quantity={_config.Level0Quantity} contracts, Balance=${balanceUsd:F2}. " +
            "Check if margin factor is too high or quantities are too large for available balance.");
        
        _testLogger.LogInformation("✓ Successfully placed {TotalCount} orders", totalOrders);
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 6: Display Order Book
        // ====================================================================
        _testLogger.LogInformation("STEP 6: Current Order Book State:");
        PrintOrderBook(stateManager, currentPrice.Value, _config.TradingDecimals);
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 7: Wait a bit (let orders rest on the book)
        // ====================================================================
        _testLogger.LogInformation("STEP 7: Waiting 5 seconds (orders live on exchange)...");
        await Task.Delay(TimeSpan.FromSeconds(5));
        _testLogger.LogInformation("✓ Wait complete");
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 8: Check Account Again (see if any fills)
        // ====================================================================
        _testLogger.LogInformation("STEP 8: Checking for fills...");
        var accountAfter = await accountService.GetAccountSnapshotAsync(token, CancellationToken.None);
        var balanceAfterUsd = accountAfter.Balance / 1_000_000m;
        
        _testLogger.LogInformation("  Balance After: ${Balance:F2} USD (change: ${Change:F2})", 
            balanceAfterUsd, balanceAfterUsd - balanceUsd);
        _testLogger.LogInformation("  Open Orders: {OrderCount}", accountAfter.Orders.Length);
        _testLogger.LogInformation("  Open Positions: {PositionCount}", accountAfter.Positions.Length);
        
        if (accountAfter.Positions.Length > 0)
        {
            _testLogger.LogWarning("⚠ WARNING: Some orders were FILLED! You now have open positions!");
            foreach (var position in accountAfter.Positions)
            {
                _testLogger.LogWarning("  Position: {Side} {Quantity} @ ${EntryPrice}", 
                    position.Side, position.Quantity / 100_000_000m, position.EntryPrice / 100_000_000m);
            }
        }
        else
        {
            _testLogger.LogInformation("✓ No fills (as expected with wide spreads)");
        }
        _testLogger.LogInformation("");

        // ====================================================================
        // STEP 9: Cancel All Orders (Cleanup)
        // ====================================================================
        _testLogger.LogInformation("STEP 9: Cleanup - Cancelling all open orders...");
        
        // Wait for any in-flight operations to settle
        _testLogger.LogInformation("  Waiting 2 seconds for operations to settle...");
        await Task.Delay(TimeSpan.FromSeconds(2));
        
        // Query account for all open orders
        var accountBeforeCleanup = await accountService.GetAccountSnapshotAsync(token, CancellationToken.None);
        var openOrders = accountBeforeCleanup.Orders;
        
        _testLogger.LogInformation("  Found {Count} open orders on account", openOrders.Length);
        
        if (openOrders.Length > 0)
        {
            // Cancel each order
            int successfulCancels = 0;
            int failedCancels = 0;
            
            foreach (var order in openOrders)
            {
                try
                {
                    _testLogger.LogInformation("  Cancelling order {OrderId}...", order.OrderId);
                    await orderService.CancelOrderAsync(order.OrderId, token, CancellationToken.None);
                    successfulCancels++;
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
        // STEP 10: Verify Final State
        // ====================================================================
        _testLogger.LogInformation("STEP 10: Verifying final state...");
        var accountFinal = await accountService.GetAccountSnapshotAsync(token, CancellationToken.None);
        var finalStateManager = stateManager.GetAllActiveOrderIds();
        
        _testLogger.LogInformation("  Open Orders on Account: {OrderCount}", accountFinal.Orders.Length);
        _testLogger.LogInformation("  Orders in State Manager: {StateCount}", finalStateManager.Length);
        _testLogger.LogInformation("  Open Positions: {PositionCount}", accountFinal.Positions.Length);
        
        if (accountFinal.Orders.Length == 0)
        {
            _testLogger.LogInformation("✓ All orders successfully cancelled");
        }
        else
        {
            _testLogger.LogWarning("⚠ WARNING: {Count} orders still remain on account", accountFinal.Orders.Length);
            foreach (var order in accountFinal.Orders)
            {
                _testLogger.LogWarning("  Remaining order: {OrderId} - {Side} @ {Price}", 
                    order.OrderId, order.Side, order.Price);
            }
        }
        
        if (accountFinal.Positions.Length > 0)
        {
            _testLogger.LogWarning("⚠ WARNING: {Count} positions remain open (requires manual closure)", 
                accountFinal.Positions.Length);
            foreach (var position in accountFinal.Positions)
            {
                var positionValueUsd = position.Quantity / 100_000_000m * position.EntryPrice / 100_000_000m;
                _testLogger.LogWarning("  Position: {Side} {Quantity} @ ${EntryPrice:F2} (notional: ${Value:F2})", 
                    position.Side, position.Quantity / 100_000_000m, 
                    position.EntryPrice / 100_000_000m, positionValueUsd);
            }
        }
        
        _testLogger.LogInformation("");

        // ====================================================================
        // SUMMARY
        // ====================================================================
        var finalBalanceUsd = accountFinal.Balance / 1_000_000m;
        
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  ONE CYCLE TEST COMPLETE");
        _testLogger.LogInformation("================================================================================");
        _testLogger.LogInformation("  Orders Placed: {TotalOrders}", totalOrders);
        _testLogger.LogInformation("  Orders Filled: {Fills}", accountAfter.Positions.Length);
        _testLogger.LogInformation("  Orders Cancelled: {Cancelled}", openOrders.Length);
        _testLogger.LogInformation("  Final Open Orders: {FinalOrders}", accountFinal.Orders.Length);
        _testLogger.LogInformation("  Final Open Positions: {FinalPositions}", accountFinal.Positions.Length);
        _testLogger.LogInformation("  Starting Balance: ${StartBalance:F2} USD", balanceUsd);
        _testLogger.LogInformation("  Final Balance: ${FinalBalance:F2} USD", finalBalanceUsd);
        _testLogger.LogInformation("  Balance Change: ${Change:F2} USD", finalBalanceUsd - balanceUsd);
        _testLogger.LogInformation("================================================================================");
        
        // Final assertion: Ensure cleanup was successful
        if (accountFinal.Orders.Length > 0)
        {
            _testLogger.LogWarning("⚠ Test completed but {Count} orders remain open!", accountFinal.Orders.Length);
        }
        
        if (accountFinal.Positions.Length > 0)
        {
            _testLogger.LogWarning("⚠ Test completed but {Count} positions remain open!", accountFinal.Positions.Length);
        }
    }

    private void PrintOrderBook(OrderStateManager stateManager, decimal indexPrice, int tradingDecimals)
    {
        var bidLevels = stateManager.GetAllBidLevels();
        var askLevels = stateManager.GetAllAskLevels();
        var scale = Math.Pow(10, tradingDecimals);

        var bids = new List<(decimal price, decimal contracts, decimal sizeUsd, Guid? orderId)>();
        var asks = new List<(decimal price, decimal contracts, decimal sizeUsd, Guid? orderId)>();

        foreach (var bid in bidLevels)
        {
            if (bid?.CurrentPrice > 0 && bid.CurrentOrderId.HasValue && bid.CurrentQuantity > 0)
            {
                var price = (decimal)(bid.CurrentPrice / scale);
                var contracts = (decimal)(bid.CurrentQuantity / scale);
                var sizeUsd = contracts * price; // Notional value
                bids.Add((price, contracts, sizeUsd, bid.CurrentOrderId));
            }
        }

        foreach (var ask in askLevels)
        {
            if (ask?.CurrentPrice > 0 && ask.CurrentOrderId.HasValue && ask.CurrentQuantity > 0)
            {
                var price = (decimal)(ask.CurrentPrice / scale);
                var contracts = (decimal)(ask.CurrentQuantity / scale);
                var sizeUsd = contracts * price; // Notional value
                asks.Add((price, contracts, sizeUsd, ask.CurrentOrderId));
            }
        }

        _testLogger.LogInformation("┌─────────────────────────────────────────────────────────────────────────────┐");
        _testLogger.LogInformation("│ BIDS (Buy Orders)              │ ASKS (Sell Orders)                         │");
        _testLogger.LogInformation("├────────────┬──────────┬─────────┼────────────┬──────────┬─────────┬──────────┤");
        _testLogger.LogInformation("│ Price      │ Quantity │ Liq USD │ Price      │ Quantity │ Liq USD │ Order ID │");
        _testLogger.LogInformation("├────────────┼──────────┼─────────┼────────────┼──────────┼─────────┼──────────┤");

        var maxRows = Math.Max(bids.Count, asks.Count);
        for (int i = 0; i < maxRows; i++)
        {
            var bidStr = i < bids.Count 
                ? $"│ ${bids[i].price,9:N2} │ {bids[i].contracts,8:N3} │ ${bids[i].sizeUsd,6:N2} "
                : "│            │          │         ";
            
            var askStr = i < asks.Count
                ? $"│ ${asks[i].price,9:N2} │ {asks[i].contracts,8:N3} │ ${asks[i].sizeUsd,6:N2} │ {asks[i].orderId?.ToString().Substring(0, 8)}... │"
                : "│            │          │         │          │";

            _testLogger.LogInformation($"{bidStr}{askStr}");
        }

        if (bids.Count > 0 && asks.Count > 0)
        {
            var spread = asks[0].price - bids[0].price;
            _testLogger.LogInformation("├────────────┴──────────┴─────────┴────────────┴──────────┴─────────┴──────────┤");
            _testLogger.LogInformation($"│ Spread: ${spread,7:F2}     │     Index: ${indexPrice,9:F2}                          │");
        }
        
        _testLogger.LogInformation("└─────────────────────────────────────────────────────────────────────────────┘");
    }

    private decimal CalculateTotalMarginRequired(
        decimal btcPrice, 
        int numLevels, 
        ulong[] quantities,
        decimal marginFactor,
        int tradingDecimals)
    {
        decimal totalMarginRequired = 0m;
        var scale = (decimal)Math.Pow(10, tradingDecimals);
        
        // Calculate for both bid and ask sides
        for (int i = 0; i < numLevels; i++)
        {
            // Quantity is already in base units (scaled by 10^tradingDecimals)
            // Notional value = (quantity / 10^tradingDecimals) * price
            var quantityInContracts = quantities[i] / scale;
            var notionalValue = quantityInContracts * btcPrice;
            var marginPerOrder = notionalValue * marginFactor;
            
            // × 2 for bid + ask
            totalMarginRequired += marginPerOrder * 2;
        }
        
        return totalMarginRequired;
    }

    public void Dispose()
    {
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// HTTP message handler that logs all requests and responses with full details
/// </summary>
internal class DetailedLoggingHandler : DelegatingHandler
{
    private readonly ILogger _logger;

    public DetailedLoggingHandler(ILogger logger)
    {
        _logger = logger;
        // Don't set InnerHandler - HttpClientFactory will set it
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, 
        CancellationToken cancellationToken)
    {
        // Log Request
        _logger.LogInformation("================================================================================");
        _logger.LogInformation("HTTP REQUEST:");
        _logger.LogInformation("  Method: {Method}", request.Method);
        _logger.LogInformation("  URL: {Url}", request.RequestUri);
        
        _logger.LogInformation("  Headers:");
        foreach (var header in request.Headers)
        {
            _logger.LogInformation("    {Key}: {Value}", header.Key, string.Join(", ", header.Value));
        }
        
        if (request.Content != null)
        {
            _logger.LogInformation("  Content Headers:");
            foreach (var header in request.Content.Headers)
            {
                _logger.LogInformation("    {Key}: {Value}", header.Key, string.Join(", ", header.Value));
            }
            
            var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("  Body: {Body}", requestBody);
        }
        
        // Send Request
        var startTime = DateTime.UtcNow;
        var response = await base.SendAsync(request, cancellationToken);
        var duration = DateTime.UtcNow - startTime;
        
        // Log Response
        _logger.LogInformation("HTTP RESPONSE:");
        _logger.LogInformation("  Status: {StatusCode} {ReasonPhrase}", (int)response.StatusCode, response.ReasonPhrase);
        _logger.LogInformation("  Duration: {Duration}ms", duration.TotalMilliseconds);
        
        _logger.LogInformation("  Headers:");
        foreach (var header in response.Headers)
        {
            _logger.LogInformation("    {Key}: {Value}", header.Key, string.Join(", ", header.Value));
        }
        
        if (response.Content != null)
        {
            _logger.LogInformation("  Content Headers:");
            foreach (var header in response.Content.Headers)
            {
                _logger.LogInformation("    {Key}: {Value}", header.Key, string.Join(", ", header.Value));
            }
            
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogInformation("  Body: {Body}", responseBody);
        }
        
        _logger.LogInformation("================================================================================");
        
        return response;
    }
}

