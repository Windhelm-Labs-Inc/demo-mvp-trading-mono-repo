using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;

namespace MarketMakerWorkerService.Tests.Helpers;

/// <summary>
/// Test configuration values - centralized to avoid magic numbers
/// All values loaded from appsettings.json or constants
/// </summary>
public static class TestConfiguration
{
    // Test Account Credentials (from env.example - these are public test keys)
    public static class TestAccount
    {
        public static string AccountId => Environment.GetEnvironmentVariable("TEST_HEDERA_ACCOUNT_ID") 
            ?? "0.0.6978377";
        public static string PrivateKeyDerHex => Environment.GetEnvironmentVariable("TEST_HEDERA_PRIVATE_KEY_DER_HEX")
            ?? "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b";
        public static string LedgerId => "testnet";
        public static string KeyType => "ed25519";
    }
    
    // Market Info Expected Values (from /api/v1/market/info)
    public static class MarketInfo
    {
        public static uint ChainId => 296;
        public static string LedgerId => "testnet";
        public static string TradingPair => "BTC/USD";
        public static string SettlementToken => "0.0.6891795";
        public static uint TradingDecimals => 8;
        public static uint SettlementDecimals => 6;
    }
    
    // Sample Market Data for Tests
    public static class SamplePrices
    {
        public static decimal BtcUsdPrice => 65000m;
        public static ulong BestBidBaseUnits => 6500000000000000ul;
        public static ulong BestAskBaseUnits => 6501000000000000ul;
        public static int BidCount => 10;
        public static int AskCount => 10;
    }
    
    // Redis Test Keys
    public static class Redis
    {
        public static string TestKeyPrefix => "test:market_maker:";
        public static string ConnectionCheckKey => "test:connection:check";
        public static string TestValue => "test_value";
        public static int TestKeyExpirySeconds => 10;
        // Use environment variable or fallback to Azure production Redis
        public static string ConnectionString
        {
            get
            {
                var baseConnection = Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") 
                    ?? "spot-index-cache.redis.cache.windows.net:6380,ssl=True,abortConnect=False";
                var password = Environment.GetEnvironmentVariable("REDIS_PASSWORD");
                
                if (!string.IsNullOrEmpty(password))
                {
                    return $"{baseConnection},password={password}";
                }
                return baseConnection;
            }
        }
        public const string ProductionIndexKey = "spotindex:BTC_USD";
    }
    
    // Mock API Responses
    public static class MockApi
    {
        public static string ChallengeId => "mock-challenge-123";
        public static string ChallengeMessage => "Sign this message to authenticate";
        public static string MockJwtToken => "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.mock_token";
        public static int TokenExpirySeconds => 900;
    }
    
    // Test Timeouts and Intervals
    public static class Timeouts
    {
        public static TimeSpan DefaultTestTimeout => TimeSpan.FromSeconds(30);
        public static TimeSpan ShortDelay => TimeSpan.FromMilliseconds(100);
        public static TimeSpan RedisWatchDuration => TimeSpan.FromSeconds(2);
    }
    
    /// <summary>
    /// Create test MarketMakerConfiguration for unit tests
    /// </summary>
    public static IOptions<MarketMakerConfiguration> CreateTestConfig(
        string? apiBaseUrl = null,
        string? accountId = null)
    {
        return Options.Create(new MarketMakerConfiguration
        {
            ApiBaseUrl = apiBaseUrl ?? "https://test-api.example.com",
            AccountId = accountId ?? TestAccount.AccountId,
            PrivateKeyDerHex = TestAccount.PrivateKeyDerHex,
            LedgerId = TestAccount.LedgerId,
            KeyType = TestAccount.KeyType,
            TradingDecimals = (int)MarketInfo.TradingDecimals,
            SettlementDecimals = (int)MarketInfo.SettlementDecimals
        });
    }
}

