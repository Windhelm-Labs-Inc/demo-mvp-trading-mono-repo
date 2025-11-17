namespace MarketMakerWorkerService.Utilities;

/// <summary>
/// Price calculation utilities for converting between decimal and base units,
/// and calculating bid/ask price levels for the liquidity ladder.
/// </summary>
public static class PriceCalculator
{
    /// <summary>
    /// Convert decimal price to base units
    /// Example: 65432.10 BTC/USD with 8 decimals = 6543210000000
    /// </summary>
    public static ulong ToBaseUnits(decimal value, int decimals)
    {
        if (value < 0)
            throw new ArgumentException("Value cannot be negative", nameof(value));
            
        var scaled = value * (decimal)Math.Pow(10, decimals);
        return (ulong)scaled;
    }

    /// <summary>
    /// Convert base units to decimal
    /// Example: 6543210000000 with 8 decimals = 65432.10
    /// </summary>
    public static decimal FromBaseUnits(ulong baseUnits, int decimals)
    {
        return baseUnits / (decimal)Math.Pow(10, decimals);
    }

    /// <summary>
    /// Calculate bid price levels around a mid price
    /// Returns array of prices, closest to spread first (index 0 = best bid)
    /// </summary>
    /// <param name="midPriceBase">Mid/index price in base units</param>
    /// <param name="spreadBps">Total spread in basis points (e.g., 10 bps = 0.1%)</param>
    /// <param name="levelSpacingBps">Spacing between levels in basis points</param>
    /// <param name="numLevels">Number of bid levels to calculate</param>
    public static ulong[] CalculateBidLevels(
        ulong midPriceBase,
        int spreadBps,
        int levelSpacingBps,
        int numLevels)
    {
        var levels = new ulong[numLevels];
        
        // Calculate best bid (half spread below mid)
        var halfSpreadBps = spreadBps / 2.0;
        var bestBid = (ulong)(midPriceBase * (1.0 - halfSpreadBps / 10000.0));
        
        for (int i = 0; i < numLevels; i++)
        {
            // Each level moves further from spread
            var offsetBps = (double)(levelSpacingBps * i);
            var offset = (ulong)(midPriceBase * offsetBps / 10000.0);
            levels[i] = bestBid - offset;
        }
        
        return levels;
    }

    /// <summary>
    /// Calculate ask price levels around a mid price
    /// Returns array of prices, closest to spread first (index 0 = best ask)
    /// </summary>
    /// <param name="midPriceBase">Mid/index price in base units</param>
    /// <param name="spreadBps">Total spread in basis points (e.g., 10 bps = 0.1%)</param>
    /// <param name="levelSpacingBps">Spacing between levels in basis points</param>
    /// <param name="numLevels">Number of ask levels to calculate</param>
    public static ulong[] CalculateAskLevels(
        ulong midPriceBase,
        int spreadBps,
        int levelSpacingBps,
        int numLevels)
    {
        var levels = new ulong[numLevels];
        
        // Calculate best ask (half spread above mid)
        var halfSpreadBps = spreadBps / 2.0;
        var bestAsk = (ulong)(midPriceBase * (1.0 + halfSpreadBps / 10000.0));
        
        for (int i = 0; i < numLevels; i++)
        {
            // Each level moves further from spread
            var offsetBps = (double)(levelSpacingBps * i);
            var offset = (ulong)(midPriceBase * offsetBps / 10000.0);
            levels[i] = bestAsk + offset;
        }
        
        return levels;
    }

    /// <summary>
    /// Calculate bid price levels using fixed USD spread and spacing
    /// Returns array of prices, closest to spread first (index 0 = best bid)
    /// </summary>
    /// <param name="midPriceBase">Mid/index price in base units</param>
    /// <param name="spreadUsd">Total spread in USD (e.g., 1.00 = $1 spread)</param>
    /// <param name="levelSpacingUsd">Spacing between levels in USD (e.g., 0.10 = $0.10)</param>
    /// <param name="numLevels">Number of bid levels to calculate</param>
    /// <param name="decimals">Number of decimals for the trading pair</param>
    public static ulong[] CalculateBidLevelsUsd(
        ulong midPriceBase,
        decimal spreadUsd,
        decimal levelSpacingUsd,
        int numLevels,
        int decimals)
    {
        var levels = new ulong[numLevels];
        var midPriceDecimal = FromBaseUnits(midPriceBase, decimals);
        
        // Calculate best bid (half spread below mid)
        var halfSpread = spreadUsd / 2m;
        var bestBid = midPriceDecimal - halfSpread;
        
        for (int i = 0; i < numLevels; i++)
        {
            // Each level moves further from spread
            var offset = levelSpacingUsd * i;
            var levelPrice = bestBid - offset;
            levels[i] = ToBaseUnits(levelPrice, decimals);
        }
        
        return levels;
    }

    /// <summary>
    /// Calculate ask price levels using fixed USD spread and spacing
    /// Returns array of prices, closest to spread first (index 0 = best ask)
    /// </summary>
    /// <param name="midPriceBase">Mid/index price in base units</param>
    /// <param name="spreadUsd">Total spread in USD (e.g., 1.00 = $1 spread)</param>
    /// <param name="levelSpacingUsd">Spacing between levels in USD (e.g., 0.10 = $0.10)</param>
    /// <param name="numLevels">Number of ask levels to calculate</param>
    /// <param name="decimals">Number of decimals for the trading pair</param>
    public static ulong[] CalculateAskLevelsUsd(
        ulong midPriceBase,
        decimal spreadUsd,
        decimal levelSpacingUsd,
        int numLevels,
        int decimals)
    {
        var levels = new ulong[numLevels];
        var midPriceDecimal = FromBaseUnits(midPriceBase, decimals);
        
        // Calculate best ask (half spread above mid)
        var halfSpread = spreadUsd / 2m;
        var bestAsk = midPriceDecimal + halfSpread;
        
        for (int i = 0; i < numLevels; i++)
        {
            // Each level moves further from spread
            var offset = levelSpacingUsd * i;
            var levelPrice = bestAsk + offset;
            levels[i] = ToBaseUnits(levelPrice, decimals);
        }
        
        return levels;
    }

    /// <summary>
    /// Calculate margin required for a limit order
    /// </summary>
    /// <param name="price">Order price in base units</param>
    /// <param name="quantity">Order quantity in base units</param>
    /// <param name="marginFactor">Margin factor (e.g., 1_200_000 = 1.2x = 120%)</param>
    /// <param name="tradingDecimals">Trading pair decimals (e.g., 8 for BTC/USD)</param>
    /// <param name="settlementDecimals">Settlement token decimals (e.g., 6 for USDC)</param>
    /// <returns>Required margin in settlement token base units</returns>
    public static ulong CalculateMargin(
        ulong price,
        ulong quantity,
        ulong marginFactor,
        int tradingDecimals,
        int settlementDecimals)
    {
        // Convert to decimal for calculation
        var priceDecimal = FromBaseUnits(price, tradingDecimals);
        var quantityDecimal = FromBaseUnits(quantity, tradingDecimals);
        var marginFactorDecimal = marginFactor / 1_000_000m; // Factor is stored as 1_000_000 = 1.0x
        
        // Notional value = price * quantity
        var notional = priceDecimal * quantityDecimal;
        
        // Margin = notional * marginFactor
        var margin = notional * marginFactorDecimal;
        
        // Convert back to settlement token base units
        return ToBaseUnits(margin, settlementDecimals);
    }

    /// <summary>
    /// Calculate total capital required for a liquidity ladder
    /// </summary>
    /// <param name="bidPrices">Bid price levels in base units</param>
    /// <param name="askPrices">Ask price levels in base units</param>
    /// <param name="quantities">Quantity at each level in base units</param>
    /// <param name="marginFactor">Margin factor</param>
    /// <param name="tradingDecimals">Trading decimals</param>
    /// <param name="settlementDecimals">Settlement decimals</param>
    /// <returns>Total margin required in settlement token base units</returns>
    public static ulong CalculateTotalCapitalRequired(
        ulong[] bidPrices,
        ulong[] askPrices,
        ulong[] quantities,
        ulong marginFactor,
        int tradingDecimals,
        int settlementDecimals)
    {
        ulong totalMargin = 0;
        
        // Calculate margin for all bid levels
        for (int i = 0; i < bidPrices.Length && i < quantities.Length; i++)
        {
            totalMargin += CalculateMargin(
                bidPrices[i],
                quantities[i],
                marginFactor,
                tradingDecimals,
                settlementDecimals);
        }
        
        // Calculate margin for all ask levels
        for (int i = 0; i < askPrices.Length && i < quantities.Length; i++)
        {
            totalMargin += CalculateMargin(
                askPrices[i],
                quantities[i],
                marginFactor,
                tradingDecimals,
                settlementDecimals);
        }
        
        return totalMargin;
    }

    /// <summary>
    /// Check if price has moved significantly enough to warrant order replacement
    /// </summary>
    /// <param name="oldPrice">Old price in base units</param>
    /// <param name="newPrice">New price in base units</param>
    /// <param name="toleranceBps">Price tolerance in basis points (e.g., 1 bps = 0.01%)</param>
    /// <returns>True if price change exceeds tolerance</returns>
    public static bool HasPriceMoved(ulong oldPrice, ulong newPrice, int toleranceBps)
    {
        if (oldPrice == 0) return true; // Always replace if no previous price
        
        var priceDiff = (ulong)Math.Abs((long)newPrice - (long)oldPrice);
        var threshold = (ulong)(oldPrice * (double)toleranceBps / 10000.0);
        
        return priceDiff > threshold;
    }
}

