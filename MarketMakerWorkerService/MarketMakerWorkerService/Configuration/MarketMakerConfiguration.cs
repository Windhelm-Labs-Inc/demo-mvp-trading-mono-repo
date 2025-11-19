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
    // Spread Configuration
    // ===========================
    /// <summary>
    /// Base spread in basis points (98 bps = ~$1 USD at $102k BTC)
    /// DEPRECATED: Use BaseSpreadUsd for fixed dollar spread
    /// </summary>
    public int BaseSpreadBps { get; set; } = 10;
    
    /// <summary>
    /// Spacing between levels in basis points (10 bps = ~$0.10 USD at $102k BTC)
    /// DEPRECATED: Use LevelSpacingUsd for fixed dollar spacing
    /// </summary>
    public int LevelSpacingBps { get; set; } = 10;
    
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
    /// Maintenance margin factor (1.0 = 100%)
    /// </summary>
    public decimal MaintenanceMarginFactor { get; set; } = 1.0m;
    
    // ===========================
    // API Rate Limiting
    // ===========================
    /// <summary>
    /// Delay between API calls to avoid rate limiting (10 seconds as observed in Python scripts)
    /// </summary>
    // public int RateLimitDelaySeconds { get; set; } = 10;
    
    // ===========================
    // Risk Management Configuration (Stage 4)
    // ===========================
    /// <summary>
    /// Maximum position size in contracts (absolute value)
    /// </summary>
    public decimal MaxPositionSize { get; set; } = 1000m;
    
    /// <summary>
    /// Maximum notional exposure in USD
    /// </summary>
    public decimal MaxNotionalExposure { get; set; } = 100_000m;
    
    /// <summary>
    /// Minimum account balance in settlement token base units (1000 USDC = 1,000,000,000)
    /// </summary>
    public decimal MinAccountBalance { get; set; } = 1000m;
    
    /// <summary>
    /// Capital utilization ratio (0.80 = use 80% of available capital)
    /// </summary>
    public decimal BalanceUtilization { get; set; } = 0.80m;
    
    // ===========================
    // Inventory Management Configuration (Stage 4)
    // ===========================
    /// <summary>
    /// Target position for market neutral strategy (0 = neutral)
    /// </summary>
    public decimal TargetPosition { get; set; } = 0m;
    
    /// <summary>
    /// Skew factor for inventory management (0.5 = moderate adjustment)
    /// </summary>
    public decimal SkewFactor { get; set; } = 0.5m;
    
    /// <summary>
    /// Maximum inventory skew in basis points (50 bps)
    /// </summary>
    public int MaxSkewBps { get; set; } = 50;
    
    // ===========================
    // Volatility Monitoring Configuration (Stage 4)
    // ===========================
    /// <summary>
    /// Enable volatility-based spread adjustments
    /// </summary>
    public bool EnableVolatilityAdjustment { get; set; } = true;
    
    /// <summary>
    /// Number of price samples to track for volatility calculation
    /// </summary>
    public int VolatilityHistorySize { get; set; } = 100;
    
    // ===========================
    // Execution Configuration (Stage 4)
    // ===========================
    /// <summary>
    /// Maximum concurrent order cancellations (for batch cancels)
    /// </summary>
    public int MaxConcurrentCancels { get; set; } = 10;
}

