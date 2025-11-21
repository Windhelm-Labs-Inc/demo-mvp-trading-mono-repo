namespace MarketMakerWorkerService.Configuration;

/// <summary>
/// Central configuration for the Market Maker service
/// All sensitive values (AccountId, PrivateKeyDerHex, etc.) are loaded from environment variables via appsettings.json placeholders
/// All trading parameters are defined here with sensible defaults
/// </summary>
public class MarketMakerConfiguration
{
    // ===========================
    // API Configuration
    // ===========================
    public string ApiBaseUrl { get; set; } = string.Empty; // Set in appsettings.json
    
    // ===========================
    // Account Configuration (FROM ENV VARIABLES)
    // ===========================
    /// <summary>
    /// Hedera account ID - MUST be loaded from environment variable ${HEDERA_ACCOUNT_ID}
    /// Example: "0.0.6978377"
    /// </summary>
    public string AccountId { get; set; } = string.Empty;
    
    /// <summary>
    /// DER-encoded private key in hex format - MUST be loaded from environment variable ${HEDERA_PRIVATE_KEY_DER_HEX}
    /// Example: "302e020100300506032b6570042204205db3a68cb7831bcefb625238e7800cc9dc85aab09b2acf97537af0d9ef667d7b"
    /// </summary>
    public string PrivateKeyDerHex { get; set; } = string.Empty;
    
    /// <summary>
    /// Ledger ID (testnet, mainnet) - loaded from environment variable ${HEDERA_LEDGER_ID}
    /// </summary>
    public string LedgerId { get; set; } = "testnet";
    
    /// <summary>
    /// Key type for signing (ed25519) - set in appsettings.json
    /// </summary>
    public string KeyType { get; set; } = "ed25519";
    
    /// <summary>
    /// Interval in seconds to refresh authentication token proactively (default 800 seconds = ~13.3 minutes)
    /// Token expires after 15 minutes (900 seconds), so we refresh before expiry
    /// Set to 0 to disable background refresh
    /// </summary>
    public int TokenRefreshIntervalSeconds { get; set; } = 800;
    
    // ===========================
    // Market Configuration
    // ===========================
    /// <summary>
    /// Trading pair decimals (8 for BTC/USD)
    /// </summary>
    public int TradingDecimals { get; set; } = 8;
    
    /// <summary>
    /// Settlement token decimals (6 for USDC)
    /// </summary>
    public int SettlementDecimals { get; set; } = 6;
    
    // ===========================
    // Liquidity Ladder Configuration
    // ===========================
    /// <summary>
    /// Number of levels on each side (bids and asks)
    /// </summary>
    public int NumberOfLevels { get; set; } = 4;
    
    /// <summary>
    /// Quantity for level 0 (nearest to spread) in contracts
    /// </summary>
    public decimal Level0Quantity { get; set; } = 1000;
    
    /// <summary>
    /// Quantity for levels 1-2 in contracts
    /// </summary>
    public decimal Levels1To2Quantity { get; set; } = 100;
    
    /// <summary>
    /// Quantity for levels 3-9 in contracts
    /// </summary>
    public decimal Levels3To9Quantity { get; set; } = 10;
    
    // ===========================
    // Spread Configuration (Fixed Dollar Amount Strategy)
    // ===========================
    /// <summary>
    /// Base spread in USD (fixed dollar amount)
    /// Example: 1.00 = $1.00 spread between best bid and best ask
    /// </summary>
    public decimal BaseSpreadUsd { get; set; } = 100.00m;
    
    /// <summary>
    /// Spacing between levels in USD (fixed dollar amount)
    /// Example: 0.10 = $0.10 between each level
    /// </summary>
    public decimal LevelSpacingUsd { get; set; } = 0.10m;
    
    // Price tolerance removed – strategy now always replaces orders when price changes
    
    // ===========================
    // Redis Configuration
    // ===========================
    /// <summary>
    /// Redis connection string - loaded from environment variable ${REDIS_CONNECTION_STRING}
    /// </summary>
    public string RedisConnectionString { get; set; } = string.Empty;
    
    /// <summary>
    /// Redis key to watch for index price updates
    /// Example: "spotindex:BTC_USD"
    /// </summary>
    public string RedisIndexKey { get; set; } = "spotindex:BTC_USD";
    
    /// <summary>
    /// Polling interval for Redis price updates in milliseconds
    /// </summary>
    public int RedisPollIntervalMs { get; set; } = 50;
    
    // ===========================
    // Margin Configuration
    // ===========================
    /// <summary>
    /// Initial margin factor for order submission
    /// This is a FACTOR/FRACTION of notional value, not an absolute amount
    /// 
    /// Examples:
    /// - 0.1 = 10% margin = 10x leverage (production minimum)
    /// - 0.2 = 20% margin = 5x leverage (recommended for testing)
    /// - 1.0 = 100% margin = 1x leverage (no leverage)
    /// - 1.2 = 120% margin = 0.83x leverage (production default - very conservative)
    /// 
    /// The value is scaled by 10^6 when sent to API (e.g., 0.2 → 200,000 base units)
    /// </summary>
    public decimal InitialMarginFactor { get; set; } = 0.2m;
    
    /// <summary>
    /// Capital utilization ratio (0.80 = use 80% of available capital)
    /// </summary>
    public decimal BalanceUtilization { get; set; } = 0.80m;
    
    // ===========================
    // Order Update Strategy
    // ===========================
    /// <summary>
    /// Order update behavior flag
    /// 1 = Atomic replacement (submit new orders first, then cancel old - maintains continuous liquidity)
    /// 0 = Sequential replacement (cancel old orders first, then submit new - creates temporary gaps)
    /// Default: 1 (traditional sequential behavior)
    /// </summary>
    public int UpdateBehaviorFlag { get; set; } = 1;
    
    /// <summary>
    /// Delay in milliseconds between submitting new orders and canceling old orders in atomic mode
    /// This gives the orderbook time to process new orders before canceling old ones
    /// Recommended: 100-500ms for production, 0 to disable
    /// Only applies when UpdateBehaviorFlag = 1 (atomic mode)
    /// </summary>
    public int AtomicReplacementDelayMs { get; set; } = 125;
}

