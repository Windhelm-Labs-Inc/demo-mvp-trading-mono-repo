using System.Reactive.Linq;
using System.Text;
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
using MarketMakerWorkerService.Utilities;

namespace MarketMakerWorkerService.Tests.E2E;

/// <summary>
/// Special E2E Behavior Demo - Shows detailed output for each index update
/// Displays: Index Update → API Calls (headers/bodies) → Order Book State
/// </summary>
public class MarketMakerBehaviorDemo : IDisposable
{
    private readonly WireMockServer _mockServer;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly MarketMakerConfiguration _config;

    public MarketMakerBehaviorDemo()
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
                ["MarketMaker:BaseSpreadBps"] = "98",  // DEPRECATED: kept for backwards compatibility
                ["MarketMaker:LevelSpacingBps"] = "10", // DEPRECATED: kept for backwards compatibility
                ["MarketMaker:BaseSpreadUsd"] = "1.00",  // FIXED $1.00 USD spread
                ["MarketMaker:LevelSpacingUsd"] = "0.10", // FIXED $0.10 USD spacing between levels
                ["MarketMaker:InitialMarginFactor"] = "0.1",
                ["MarketMaker:RateLimitDelaySeconds"] = "0",  // No delays - APIs are mocked!
                ["MarketMaker:BalanceUtilization"] = "0.8",
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
        
        // Logging - minimal output from services
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Warning);
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
        
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task E2E_BehaviorDemo_DetailedOutput()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("  Monolith Market Maker MVP Demo - E2E TEST BEHAVIOR");
        Console.WriteLine("================================================================================");
        Console.WriteLine("  Redis: PRODUCTION (read-only) - spotindex:BTC_USD");
        Console.WriteLine("  APIs: MOCKED with detailed request/response logging");
        Console.WriteLine("  Duration: 60 seconds");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        // Create output buffer
        var outputBuffer = new OutputBuffer();
        
        // Create API call tracker
        var apiCallTracker = new ApiCallTracker();

        // Setup mocks
        SetupAuthMock();
        SetupOrderMocks();
        SetupAccountMocks();

        // Create logging wrappers
        var loggingOrderService = new LoggingOrderService(
            _serviceProvider.GetRequiredService<IHttpClientFactory>(),
            Options.Create(_config),
            _serviceProvider.GetRequiredService<ILogger<OrderService>>(),
            outputBuffer,
            apiCallTracker);

        var loggingAuthService = new LoggingAuthenticationService(
            _serviceProvider.GetRequiredService<IHttpClientFactory>(),
            Options.Create(_config),
            _serviceProvider.GetRequiredService<ILogger<AuthenticationService>>(),
            outputBuffer,
            apiCallTracker);

        var redisWatcher = _serviceProvider.GetRequiredService<RedisIndexWatcher>();
        var stateManager = _serviceProvider.GetRequiredService<OrderStateManager>();

        // Create logging strategy
        var strategy = new LoggingMarketMakerStrategy(
            loggingOrderService,
            loggingAuthService,
            stateManager,
            Options.Create(_config),
            _serviceProvider.GetRequiredService<ILogger<BasicMarketMakerStrategy>>(),
            outputBuffer);

        strategy.Initialize();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var updateCount = 0;
        var updateSemaphore = new SemaphoreSlim(1, 1);

        // Subscribe to price updates
        var priceObservable = redisWatcher.CreatePriceObservable(
            _config.RedisIndexKey,
            _config.RedisPollIntervalMs,
            cts.Token);

        Console.WriteLine("Watching Redis price updates for 60 seconds...");
        Console.WriteLine();

        var subscription = priceObservable
            .Subscribe(
                onNext: async update =>
                {
                    try
                    {
                        // Ensure only one update processes at a time
                        await updateSemaphore.WaitAsync(cts.Token);
                        try
                        {
                            updateCount++;
                            
                            // Clear buffer and start new update
                            outputBuffer.Clear();
                            outputBuffer.StartUpdate(updateCount, update.Price, update.Timestamp);

                            try
                            {
                                // Process through strategy (logs to buffer)
                                await strategy.ProcessPriceUpdateAsync(update.Price, cts.Token);
                            }
                            catch (TaskCanceledException)
                            {
                                // Cancellation during processing - add note to buffer
                                outputBuffer.AddLine("(Update cancelled - test timeout)");
                            }
                            catch (Exception ex)
                            {
                                outputBuffer.AddError($"Error: {ex.Message}");
                            }
                            finally
                            {
                                // ALWAYS print buffered output, even if cancelled or errored
                                outputBuffer.PrintBuffer(stateManager, update.Price, _config.TradingDecimals);
                            }
                        }
                        finally
                        {
                            updateSemaphore.Release();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Suppress cancellation during shutdown (updates queued when test ends)
                    }
                },
                onError: ex => Console.WriteLine($"Error: {ex.Message}"),
                onCompleted: () => Console.WriteLine("Completed"));

        // Wait for 60 seconds
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException) { }

        subscription.Dispose();

        Console.WriteLine();
        Console.WriteLine("================================================================================");
        Console.WriteLine($"  DEMO COMPLETE - Processed {updateCount} updates");
        Console.WriteLine("================================================================================");
        Console.WriteLine();
        
        // Print API call summary
        apiCallTracker.PrintSummary();

        updateCount.Should().BeGreaterThan(0);
    }

    #region Helper Classes

    private class ApiCallTracker
    {
        private int _authChallengeCount = 0;
        private int _authVerifyCount = 0;
        private int _orderSubmitCount = 0;
        private int _orderCancelCount = 0;
        private readonly object _lock = new();

        public void RecordAuthChallenge() { lock (_lock) _authChallengeCount++; }
        public void RecordAuthVerify() { lock (_lock) _authVerifyCount++; }
        public void RecordOrderSubmit() { lock (_lock) _orderSubmitCount++; }
        public void RecordOrderCancel() { lock (_lock) _orderCancelCount++; }

        public void PrintSummary()
        {
            lock (_lock)
            {
                Console.WriteLine("API CALL SUMMARY");
                Console.WriteLine("================================================================================");
                Console.WriteLine();
                Console.WriteLine($"  Authentication:");
                Console.WriteLine($"    - Challenge Requests:         {_authChallengeCount,6}");
                Console.WriteLine($"    - Verify Requests:            {_authVerifyCount,6}");
                Console.WriteLine($"    - Total Auth Calls:           {_authChallengeCount + _authVerifyCount,6}");
                Console.WriteLine();
                Console.WriteLine($"  Order Management:");
                Console.WriteLine($"    - Order Submissions:          {_orderSubmitCount,6}");
                Console.WriteLine($"    - Order Cancellations:        {_orderCancelCount,6}");
                Console.WriteLine($"    - Total Order Calls:          {_orderSubmitCount + _orderCancelCount,6}");
                Console.WriteLine();
                Console.WriteLine($"  TOTAL API CALLS:                {_authChallengeCount + _authVerifyCount + _orderSubmitCount + _orderCancelCount,6}");
                Console.WriteLine();
                Console.WriteLine("================================================================================");
            }
        }
    }

    private class OutputBuffer
    {
        private readonly List<string> _lines = new();
        private readonly object _lock = new();
        private int _updateNumber;
        private decimal _indexPrice;
        private DateTime _timestamp;

        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
            }
        }

        public void StartUpdate(int updateNumber, decimal indexPrice, DateTime timestamp)
        {
            _updateNumber = updateNumber;
            _indexPrice = indexPrice;
            _timestamp = timestamp;
        }

        public void AddLine(string line)
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                _lines.Add($"[{now:HH:mm:ss.fff}] {line}");
            }
        }

        public void AddError(string error)
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                _lines.Add($"[{now:HH:mm:ss.fff}] ERROR: {error}");
            }
        }

        public void PrintBuffer(OrderStateManager stateManager, decimal indexPrice, int tradingDecimals)
        {
            Console.WriteLine();
            Console.WriteLine("================================================================================");
            Console.WriteLine($"UPDATE #{_updateNumber} @ ${_indexPrice:F2} - {_timestamp:HH:mm:ss.fff}");
            Console.WriteLine("================================================================================");
            
            // Make a thread-safe copy of lines
            List<string> linesCopy;
            lock (_lock)
            {
                linesCopy = new List<string>(_lines);
            }
            
            // Print buffered lines (API calls)
            if (linesCopy.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("--- API CALLS ---");
                foreach (var line in linesCopy)
                {
                    Console.WriteLine(line);
                }
            }

            // Print order book
            Console.WriteLine();
            Console.WriteLine("--- ORDER BOOK STATE ---");
            Console.WriteLine();
            PrintOrderBook(stateManager, indexPrice, tradingDecimals);
            Console.WriteLine();
        }

        private void PrintOrderBook(OrderStateManager stateManager, decimal indexPrice, int tradingDecimals)
        {
            var bidLevels = stateManager.GetAllBidLevels();
            var askLevels = stateManager.GetAllAskLevels();
            var scale = Math.Pow(10, tradingDecimals);

            // Store (price, contracts, sizeUsd) for each level
            var bids = new List<(decimal price, decimal contracts, decimal sizeUsd)>();
            var asks = new List<(decimal price, decimal contracts, decimal sizeUsd)>();

            for (int i = 0; i < bidLevels.Length; i++)
            {
                var bid = bidLevels[i];
                if (bid?.CurrentPrice > 0 && bid.CurrentOrderId.HasValue && bid.CurrentQuantity > 0)
                {
                    var price = (decimal)(bid.CurrentPrice / scale);
                    var contracts = (decimal)(bid.CurrentQuantity / scale);
                    // 1 contract = 0.0001 BTC (10^-4 BTC)
                    var btcAmount = contracts * 0.0001m;
                    var sizeUsd = btcAmount * price;
                    bids.Add((price, contracts, sizeUsd));
                }
            }

            for (int i = 0; i < askLevels.Length; i++)
            {
                var ask = askLevels[i];
                if (ask?.CurrentPrice > 0 && ask.CurrentOrderId.HasValue && ask.CurrentQuantity > 0)
                {
                    var price = (decimal)(ask.CurrentPrice / scale);
                    var contracts = (decimal)(ask.CurrentQuantity / scale);
                    // 1 contract = 0.0001 BTC (10^-4 BTC)
                    var btcAmount = contracts * 0.0001m;
                    var sizeUsd = btcAmount * price;
                    asks.Add((price, contracts, sizeUsd));
                }
            }

            // Print order book header
            Console.WriteLine("Orderbook");
            Console.WriteLine();

            // Column headers
            var header = string.Format("{0,-15} {1,20} {2,15} | {3,15} {4,20} {5,15}",
                "Price", "Size (# Contracts)", "Liq (USD)",
                "Price", "Size (# Contracts)", "Liq (USD)");
            Console.WriteLine(header);
            Console.WriteLine(new string('-', header.Length));

            // Asks are already in correct order (level 0 = best ask = lowest price = top of book)
            // Bids are in correct order (level 0 = best bid = highest price = top of book)
            
            // Determine how many rows to print
            var maxRows = Math.Max(bids.Count, asks.Count);
            maxRows = Math.Min(maxRows, 10); // Limit to 10 rows

            for (int i = 0; i < maxRows; i++)
            {
                // Bid side (left)
                string bidPriceStr = "", bidContractsStr = "", bidSizeStr = "";
                if (i < bids.Count)
                {
                    bidPriceStr = bids[i].price.ToString("N2");
                    bidContractsStr = bids[i].contracts.ToString("N0");
                    bidSizeStr = "$" + bids[i].sizeUsd.ToString("N2");
                }

                // Ask side (right)
                string askPriceStr = "", askContractsStr = "", askSizeStr = "";
                if (i < asks.Count)
                {
                    askPriceStr = asks[i].price.ToString("N2");
                    askContractsStr = asks[i].contracts.ToString("N0");
                    askSizeStr = "$" + asks[i].sizeUsd.ToString("N2");
                }

                Console.WriteLine("{0,-15} {1,20} {2,15} | {3,15} {4,20} {5,15}",
                    bidPriceStr, bidContractsStr, bidSizeStr,
                    askPriceStr, askContractsStr, askSizeStr);
            }

            // Print spread in the middle
            if (bids.Count > 0 && asks.Count > 0)
            {
                Console.WriteLine();
                var spread = asks[0].price - bids[0].price;
                Console.WriteLine($"Spread: ${spread:F2}");
            }
        }
    }

    private class LoggingOrderService : IOrderService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MarketMakerConfiguration _config;
        private readonly ILogger<OrderService> _logger;
        private readonly OutputBuffer _buffer;
        private readonly ApiCallTracker _tracker;

        public LoggingOrderService(IHttpClientFactory factory, IOptions<MarketMakerConfiguration> config, ILogger<OrderService> logger, OutputBuffer buffer, ApiCallTracker tracker)
        {
            _httpClientFactory = factory;
            _config = config.Value;
            _logger = logger;
            _buffer = buffer;
            _tracker = tracker;
        }

        public async Task<SubmitOrderResponse> SubmitLimitOrderAsync(ContractSide side, ulong price, ulong quantity, ulong marginFactor, string clientOrderId, string jwtToken, CancellationToken cancellationToken)
        {
            _tracker.RecordOrderSubmit();
            
            var priceDecimal = price / 100_000_000.0m;
            var qtyDecimal = quantity / 100_000_000.0m;
            
            _buffer.AddLine("");
            _buffer.AddLine($"POST /api/v1/orders/limit");
            _buffer.AddLine($"  Body: {{");
            _buffer.AddLine($"    \"side\": \"{side}\",");
            _buffer.AddLine($"    \"price\": {price},");
            _buffer.AddLine($"    \"quantity\": {quantity},");
            _buffer.AddLine($"    \"margin_factor\": {marginFactor},");
            _buffer.AddLine($"    \"client_order_id\": \"{clientOrderId}\"");
            _buffer.AddLine($"  }}");
            _buffer.AddLine($"  Market_Maker_Action: {side} @ ${priceDecimal:F2} x {qtyDecimal:F0} units");

            var service = new OrderService(_httpClientFactory, Options.Create(_config), _logger);
            var response = await service.SubmitLimitOrderAsync(side, price, quantity, marginFactor, clientOrderId, jwtToken, cancellationToken);

            _buffer.AddLine($"  Response: {{\"order_id\": \"{response.OrderId}\"}}");
            return response;
        }

        public async Task<CancelOrderResponse> CancelOrderAsync(Guid orderId, string jwtToken, CancellationToken cancellationToken)
        {
            _tracker.RecordOrderCancel();
            
            _buffer.AddLine("");
            _buffer.AddLine($"POST /api/v1/orders/cancel");
            _buffer.AddLine($"  Body: {{");
            _buffer.AddLine($"    \"owner_id\": \"{_config.AccountId}\",");
            _buffer.AddLine($"    \"owner_type\": \"hapi\",");
            _buffer.AddLine($"    \"order_id\": \"{orderId}\"");
            _buffer.AddLine($"  }}");
            
            var service = new OrderService(_httpClientFactory, Options.Create(_config), _logger);
            var response = await service.CancelOrderAsync(orderId, jwtToken, cancellationToken);
            
            _buffer.AddLine($"  Response: {{\"order_id\": \"{response.OrderId}\", \"unfilled_quantity\": {response.UnfilledQuantity}}}");
            return response;
        }
    }

    private class LoggingAuthenticationService : IAuthenticationService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MarketMakerConfiguration _config;
        private readonly ILogger<AuthenticationService> _logger;
        private readonly OutputBuffer _buffer;
        private readonly ApiCallTracker _tracker;
        private string? _currentToken;
        private DateTime _tokenExpiry;

        public LoggingAuthenticationService(IHttpClientFactory factory, IOptions<MarketMakerConfiguration> config, ILogger<AuthenticationService> logger, OutputBuffer buffer, ApiCallTracker tracker)
        {
            _httpClientFactory = factory;
            _config = config.Value;
            _logger = logger;
            _buffer = buffer;
            _tracker = tracker;
        }

        public async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
        {
            _tracker.RecordAuthChallenge();
            _tracker.RecordAuthVerify();
            
            _buffer.AddLine("");
            _buffer.AddLine("POST /api/v1/auth/challenge");
            _buffer.AddLine("  Body: {\"account_id\": \"" + _config.AccountId + "\"}");
            
            var service = new AuthenticationService(_httpClientFactory, Options.Create(_config), _logger);
            var token = await service.AuthenticateAsync(cancellationToken);
            
            _currentToken = token;
            _tokenExpiry = DateTime.UtcNow.AddMinutes(15);
            
            _buffer.AddLine("  Response: {\"challenge_id\": \"...\"}");
            _buffer.AddLine("");
            _buffer.AddLine("POST /api/v1/auth/verify");
            _buffer.AddLine("  Body: {\"challenge_id\": \"...\", \"signature\": \"...\"}");
            _buffer.AddLine("  Response: {\"access_token\": \"***\", \"expires_in\": 900}");
            
            return token;
        }

        public async Task<string> GetValidTokenAsync(CancellationToken cancellationToken)
        {
            if (_currentToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _currentToken;
            return await AuthenticateAsync(cancellationToken);
        }
    }

    private class LoggingMarketMakerStrategy
    {
        private readonly LoggingOrderService _orderService;
        private readonly LoggingAuthenticationService _authService;
        private readonly OrderStateManager _stateManager;
        private readonly MarketMakerConfiguration _config;
        private readonly ILogger _logger;
        private readonly OutputBuffer _buffer;
        private readonly LiquidityShape _liquidityShape;

        public LoggingMarketMakerStrategy(LoggingOrderService orderService, LoggingAuthenticationService authService, 
            OrderStateManager stateManager, IOptions<MarketMakerConfiguration> config, ILogger logger, OutputBuffer buffer)
        {
            _orderService = orderService;
            _authService = authService;
            _stateManager = stateManager;
            _config = config.Value;
            _logger = logger;
            _buffer = buffer;
            _liquidityShape = new LiquidityShape
            {
                Level0Size = 100m,
                Level1_2Size = 50m,
                Level3_9Size = 10m
            };
        }

        public void Initialize() => _stateManager.InitializeLadder(10);

        public async Task ProcessPriceUpdateAsync(decimal indexPrice, CancellationToken cancellationToken)
        {
            _buffer.AddLine($"Index Price: ${indexPrice:F2}");
            
            var token = await _authService.GetValidTokenAsync(cancellationToken);
            
            const int numLevels = 10;
            var midPriceBase = PriceCalculator.ToBaseUnits(indexPrice, _config.TradingDecimals);
            var bidPrices = PriceCalculator.CalculateBidLevelsUsd(midPriceBase, _config.BaseSpreadUsd, _config.LevelSpacingUsd, numLevels, _config.TradingDecimals);
            var askPrices = PriceCalculator.CalculateAskLevelsUsd(midPriceBase, _config.BaseSpreadUsd, _config.LevelSpacingUsd, numLevels, _config.TradingDecimals);
            var quantities = LiquidityShapeCalculator.CalculateQuantities(_liquidityShape, _config.TradingDecimals, numLevels);
            
            var replacements = _stateManager.CalculateReplacements(bidPrices, askPrices, quantities);
            
            _buffer.AddLine($"Orders to replace: {replacements.Count}");
            
            if (replacements.Count > 0)
            {
                await ExecuteReplacementsAsync(replacements, token, cancellationToken);
            }
            else
            {
                _buffer.AddLine("No order changes required - all orders already at target prices");
            }
        }

        private async Task ExecuteReplacementsAsync(List<OrderReplacement> replacements, string jwtToken, CancellationToken cancellationToken)
        {
            // Cancel old orders
            foreach (var r in replacements.Where(x => x.OldOrderId.HasValue))
            {
                try
                {
                    await _orderService.CancelOrderAsync(r.OldOrderId!.Value, jwtToken, cancellationToken);
                    _stateManager.ClearLevel(r.Side, r.LevelIndex);
                }
                catch { }
                await Task.Delay(_config.RateLimitDelaySeconds * 1000, cancellationToken);
            }

            // Place new orders
            foreach (var r in replacements)
            {
                try
                {
                    var marginFactor = (ulong)(_config.InitialMarginFactor * 1_000_000);
                    var clientOrderId = Guid.NewGuid().ToString();
                    var response = await _orderService.SubmitLimitOrderAsync(r.Side, r.NewPrice, r.NewQuantity, marginFactor, clientOrderId, jwtToken, cancellationToken);
                    _stateManager.UpdateLevel(r.Side, r.LevelIndex, response.OrderId, r.NewPrice, r.NewQuantity);
                }
                catch { }
                await Task.Delay(_config.RateLimitDelaySeconds * 1000, cancellationToken);
            }
        }
    }

    #endregion

    #region Mock Setup

    private void SetupAuthMock()
    {
        _mockServer.Given(Request.Create().WithPath("/api/v1/auth/challenge").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"challenge_id\":\"mock-{DateTime.UtcNow.Ticks}\",\"message\":\"Sign this\",\"expires_at_utc\":\"{DateTime.UtcNow.AddMinutes(5):O}\"}}"));
        
        _mockServer.Given(Request.Create().WithPath("/api/v1/auth/verify").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"access_token\":\"mock-token-{DateTime.UtcNow.Ticks}\",\"expires_in\":900}}"));
    }

    private void SetupOrderMocks()
    {
        _mockServer.Given(Request.Create().WithPath("/api/v1/orders/limit").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"order_id\":\"{Guid.NewGuid()}\",\"order_status\":\"open\",\"quantity_filled\":0,\"trade_id\":null,\"position_ids\":[]}}"));
        
        _mockServer.Given(Request.Create().WithPath("/api/v1/orders/cancel").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"order_id\":\"{Guid.NewGuid()}\",\"unfilled_quantity\":0}}"));
    }

    private void SetupAccountMocks()
    {
        _mockServer.Given(Request.Create().WithPath("/api/v1/account").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"owner_id\":\"0.0.6978377\",\"balance\":100000000000,\"orders\":[],\"positions\":[]}"));
    }

    #endregion

    public void Dispose()
    {
        _mockServer?.Stop();
        _mockServer?.Dispose();
        if (_serviceProvider is IDisposable disposable) disposable.Dispose();
    }
}
