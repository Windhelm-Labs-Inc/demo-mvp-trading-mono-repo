using MarketMakerWorkerService.Models;

namespace MarketMakerWorkerService.Utilities;

/// <summary>
/// Calculator for shaped liquidity ladder quantities (100/50/50/10×7 pattern)
/// Level 0: 100 contracts (nearest to spread)
/// Levels 1-2: 50 contracts each
/// Levels 3-9: 10 contracts each
/// Total per side: 270 contracts
/// </summary>
public static class LiquidityShapeCalculator
{
    /// <summary>
    /// Default liquidity shape: 100/50/50/10×7
    /// </summary>
    public static readonly LiquidityShape DefaultShape = new()
    {
        Level0Size = 100m,
        Level1_2Size = 50m,
        Level3_9Size = 10m
    };
    
    /// <summary>
    /// Calculate quantities for all levels in the ladder
    /// Returns array of quantities in base units
    /// </summary>
    /// <param name="shape">Liquidity shape specification</param>
    /// <param name="tradingDecimals">Trading pair decimals (e.g., 8 for BTC/USD)</param>
    /// <param name="numLevels">Number of levels (default 10)</param>
    /// <returns>Array of quantities in base units, index 0 = closest to spread</returns>
    public static ulong[] CalculateQuantities(
        LiquidityShape shape,
        int tradingDecimals,
        int numLevels = 10)
    {
        var quantities = new ulong[numLevels];
        
        for (int i = 0; i < numLevels; i++)
        {
            var sizeDecimal = shape.GetSizeForLevel(i);
            quantities[i] = PriceCalculator.ToBaseUnits(sizeDecimal, tradingDecimals);
        }
        
        return quantities;
    }
    
    /// <summary>
    /// Calculate quantities with optional scaling factor
    /// Useful for adjusting sizes based on available capital
    /// </summary>
    /// <param name="shape">Liquidity shape specification</param>
    /// <param name="tradingDecimals">Trading pair decimals</param>
    /// <param name="scaleFactor">Scale factor (1.0 = 100%, 0.5 = 50%, etc.)</param>
    /// <param name="numLevels">Number of levels</param>
    /// <returns>Scaled quantities in base units</returns>
    public static ulong[] CalculateScaledQuantities(
        LiquidityShape shape,
        int tradingDecimals,
        decimal scaleFactor,
        int numLevels = 10)
    {
        if (scaleFactor <= 0)
            throw new ArgumentException("Scale factor must be positive", nameof(scaleFactor));
            
        var quantities = new ulong[numLevels];
        
        for (int i = 0; i < numLevels; i++)
        {
            var sizeDecimal = shape.GetSizeForLevel(i) * scaleFactor;
            quantities[i] = PriceCalculator.ToBaseUnits(sizeDecimal, tradingDecimals);
        }
        
        return quantities;
    }
    
    /// <summary>
    /// Calculate quantity for a specific level
    /// </summary>
    /// <param name="levelIndex">Level index (0-9)</param>
    /// <param name="shape">Liquidity shape</param>
    /// <param name="tradingDecimals">Trading decimals</param>
    /// <returns>Quantity in base units</returns>
    public static ulong GetQuantityForLevel(
        int levelIndex,
        LiquidityShape shape,
        int tradingDecimals)
    {
        var sizeDecimal = shape.GetSizeForLevel(levelIndex);
        return PriceCalculator.ToBaseUnits(sizeDecimal, tradingDecimals);
    }
    
    /// <summary>
    /// Calculate total quantity across all levels
    /// </summary>
    /// <param name="shape">Liquidity shape</param>
    /// <param name="tradingDecimals">Trading decimals</param>
    /// <param name="numLevels">Number of levels</param>
    /// <returns>Total quantity in base units</returns>
    public static ulong CalculateTotalQuantity(
        LiquidityShape shape,
        int tradingDecimals,
        int numLevels = 10)
    {
        var quantities = CalculateQuantities(shape, tradingDecimals, numLevels);
        return quantities.Aggregate(0UL, (sum, qty) => sum + qty);
    }
    
    /// <summary>
    /// Validate that available capital is sufficient for the ladder
    /// </summary>
    /// <param name="shape">Liquidity shape</param>
    /// <param name="bidPrices">Bid prices in base units</param>
    /// <param name="askPrices">Ask prices in base units</param>
    /// <param name="availableBalance">Available balance in settlement token base units</param>
    /// <param name="marginFactor">Margin factor</param>
    /// <param name="tradingDecimals">Trading decimals</param>
    /// <param name="settlementDecimals">Settlement decimals</param>
    /// <param name="utilizationFactor">Max utilization % (e.g., 0.8 = use 80% of balance)</param>
    /// <returns>True if sufficient capital, false otherwise</returns>
    public static bool HasSufficientCapital(
        LiquidityShape shape,
        ulong[] bidPrices,
        ulong[] askPrices,
        ulong availableBalance,
        ulong marginFactor,
        int tradingDecimals,
        int settlementDecimals,
        decimal utilizationFactor = 0.8m)
    {
        var quantities = CalculateQuantities(shape, tradingDecimals);
        
        var totalMargin = PriceCalculator.CalculateTotalCapitalRequired(
            bidPrices,
            askPrices,
            quantities,
            marginFactor,
            tradingDecimals,
            settlementDecimals);
        
        var maxUsableBalance = (ulong)(availableBalance * utilizationFactor);
        
        return totalMargin <= maxUsableBalance;
    }
    
    /// <summary>
    /// Calculate maximum scale factor given available capital
    /// Returns 1.0 if full ladder can be deployed, < 1.0 if needs scaling down
    /// </summary>
    /// <param name="shape">Liquidity shape</param>
    /// <param name="bidPrices">Bid prices</param>
    /// <param name="askPrices">Ask prices</param>
    /// <param name="availableBalance">Available balance</param>
    /// <param name="marginFactor">Margin factor</param>
    /// <param name="tradingDecimals">Trading decimals</param>
    /// <param name="settlementDecimals">Settlement decimals</param>
    /// <param name="utilizationFactor">Max utilization %</param>
    /// <returns>Scale factor (0.0 to 1.0)</returns>
    public static decimal CalculateMaxScaleFactor(
        LiquidityShape shape,
        ulong[] bidPrices,
        ulong[] askPrices,
        ulong availableBalance,
        ulong marginFactor,
        int tradingDecimals,
        int settlementDecimals,
        decimal utilizationFactor = 0.8m)
    {
        var quantities = CalculateQuantities(shape, tradingDecimals);
        
        var totalMargin = PriceCalculator.CalculateTotalCapitalRequired(
            bidPrices,
            askPrices,
            quantities,
            marginFactor,
            tradingDecimals,
            settlementDecimals);
        
        if (totalMargin == 0)
            return 1.0m;
        
        var maxUsableBalance = availableBalance * utilizationFactor;
        var scaleFactor = maxUsableBalance / totalMargin;
        
        // Cap at 1.0 (never scale up, only down)
        return Math.Min(1.0m, scaleFactor);
    }
}

