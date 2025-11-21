namespace MarketMakerWorkerService.Models;

/// <summary>
/// Index price update from Redis
/// </summary>
public record IndexPriceUpdate(
    decimal Price,
    DateTime Timestamp);

/// <summary>
/// Represents a single level in the order book (bid or ask)
/// Tracks current state of orders at this level
/// </summary>
public record OrderLevel
{
    /// <summary>
    /// Level index (0 = closest to spread, 9 = furthest)
    /// </summary>
    public int LevelIndex { get; init; }
    
    /// <summary>
    /// Current order ID at this level (null if no active order)
    /// </summary>
    public Guid? CurrentOrderId { get; set; }
    
    /// <summary>
    /// Current price at this level in base units
    /// </summary>
    public ulong CurrentPrice { get; set; }
    
    /// <summary>
    /// Current quantity at this level in base units
    /// </summary>
    public ulong CurrentQuantity { get; set; }
    
    /// <summary>
    /// When this level was last updated
    /// </summary>
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Represents the complete liquidity ladder (all bids and asks)
/// </summary>
public record LiquidityLadder
{
    /// <summary>
    /// Mid/index price this ladder was calculated from
    /// </summary>
    public ulong MidPrice { get; init; }
    
    /// <summary>
    /// Bid levels (index 0 = best bid, closest to spread)
    /// </summary>
    public OrderLevel[] BidLevels { get; init; } = Array.Empty<OrderLevel>();
    
    /// <summary>
    /// Ask levels (index 0 = best ask, closest to spread)
    /// </summary>
    public OrderLevel[] AskLevels { get; init; } = Array.Empty<OrderLevel>();
    
    /// <summary>
    /// When this ladder was created
    /// </summary>
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Liquidity shape specification (100/50/50/10×7 pattern)
/// </summary>
public record LiquidityShape
{
    /// <summary>
    /// Size at level 0 (closest to spread)
    /// Default: 100 contracts
    /// </summary>
    public decimal Level0Size { get; init; } = 100m;
    
    /// <summary>
    /// Size at levels 1-2
    /// Default: 50 contracts each
    /// </summary>
    public decimal Level1_2Size { get; init; } = 50m;
    
    /// <summary>
    /// Size at levels 3-9
    /// Default: 10 contracts each
    /// </summary>
    public decimal Level3_9Size { get; init; } = 10m;
    
    /// <summary>
    /// Get the quantity for a specific level index
    /// </summary>
    public decimal GetSizeForLevel(int levelIndex)
    {
        return levelIndex switch
        {
            0 => Level0Size,
            1 or 2 => Level1_2Size,
            >= 3 and <= 9 => Level3_9Size,
            _ => throw new ArgumentException($"Invalid level index: {levelIndex}", nameof(levelIndex))
        };
    }
    
    /// <summary>
    /// Calculate total size across all levels
    /// </summary>
    public decimal TotalSize => Level0Size + (2 * Level1_2Size) + (7 * Level3_9Size); // 100 + 100 + 70 = 270
}

/// <summary>
/// Account balance and position information
/// </summary>
public record AccountSnapshot
{
    /// <summary>
    /// Available balance in settlement token base units
    /// </summary>
    public ulong Balance { get; init; }
    
    /// <summary>
    /// Active orders (open limit orders)
    /// </summary>
    public OrderInfo[] Orders { get; init; } = Array.Empty<OrderInfo>();
    
    /// <summary>
    /// Open positions
    /// </summary>
    public PositionInfo[] Positions { get; init; } = Array.Empty<PositionInfo>();
    
    /// <summary>
    /// When this snapshot was taken
    /// </summary>
    public DateTime Timestamp { get; init; }
    
    /// <summary>
    /// Calculate net position (long - short)
    /// </summary>
    public long NetPosition
    {
        get
        {
            long longQty = 0;
            long shortQty = 0;
            
            foreach (var pos in Positions)
            {
                if (pos.Side?.ToLower() == "long")
                    longQty += (long)pos.Quantity;
                else if (pos.Side?.ToLower() == "short")
                    shortQty += (long)pos.Quantity;
            }
            
            return longQty - shortQty;
        }
    }
}

/// <summary>
/// Represents an order replacement operation (cancel old, submit new)
/// </summary>
public record OrderReplacement
{
    /// <summary>
    /// Level index being replaced
    /// </summary>
    public int LevelIndex { get; init; }
    
    /// <summary>
    /// Side (Long = bid, Short = ask)
    /// </summary>
    public ContractSide Side { get; init; }
    
    /// <summary>
    /// Old order ID to cancel (null if no existing order)
    /// </summary>
    public Guid? OldOrderId { get; init; }
    
    /// <summary>
    /// New price for the order
    /// </summary>
    public ulong NewPrice { get; init; }
    
    /// <summary>
    /// New quantity for the order
    /// </summary>
    public ulong NewQuantity { get; init; }
}

/// <summary>
/// Result of a settlement operation
/// </summary>
public record SettlementResult
{
    public bool Success { get; init; }
    public string? SettlementId { get; init; }
    public decimal QuantitySettled { get; init; }
    public int PositionsSettled { get; init; }
    public string? ErrorMessage { get; init; }
    
    public static SettlementResult Settled(
        string settlementId, decimal quantity, int positions) =>
        new() { Success = true, SettlementId = settlementId, 
                QuantitySettled = quantity, PositionsSettled = positions };
    
    public static SettlementResult NoPositions() =>
        new() { Success = true, ErrorMessage = "No positions" };
    
    public static SettlementResult NoSettleable(decimal longQty, decimal shortQty) =>
        new() { Success = true, 
                ErrorMessage = $"No settleable (L:{longQty}, S:{shortQty})" };
    
    public static SettlementResult Failed(string error) =>
        new() { Success = false, ErrorMessage = error };
}