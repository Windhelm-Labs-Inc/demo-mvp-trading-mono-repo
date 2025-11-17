using FluentAssertions;
using MarketMakerWorkerService.Utilities;
using Xunit;

namespace MarketMakerWorkerService.Tests.Unit;

public class PriceCalculatorTests
{
    [Theory]
    [InlineData(65432.10, 8, 6543210000000)]
    [InlineData(1.50, 6, 1500000)]
    [InlineData(100.0, 8, 10000000000)]
    [InlineData(0.01, 8, 1000000)]
    public void ToBaseUnits_ValidDecimal_ConvertsCorrectly(decimal value, int decimals, ulong expected)
    {
        // Act
        var result = PriceCalculator.ToBaseUnits(value, decimals);
        
        // Assert
        result.Should().Be(expected);
    }
    
    [Fact]
    public void ToBaseUnits_NegativeValue_ThrowsException()
    {
        // Arrange
        var negativeValue = -100m;
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() => PriceCalculator.ToBaseUnits(negativeValue, 8));
    }
    
    [Theory]
    [InlineData(6543210000000, 8, 65432.10)]
    [InlineData(1500000, 6, 1.50)]
    [InlineData(10000000000, 8, 100.0)]
    [InlineData(1000000, 8, 0.01)]
    public void FromBaseUnits_ValidBaseUnits_ConvertsCorrectly(ulong baseUnits, int decimals, decimal expected)
    {
        // Act
        var result = PriceCalculator.FromBaseUnits(baseUnits, decimals);
        
        // Assert
        result.Should().Be(expected);
    }
    
    [Fact]
    public void RoundTrip_ToBaseUnitsAndBack_PreservesValue()
    {
        // Arrange
        var original = 65432.10m;
        var decimals = 8;
        
        // Act
        var baseUnits = PriceCalculator.ToBaseUnits(original, decimals);
        var result = PriceCalculator.FromBaseUnits(baseUnits, decimals);
        
        // Assert
        result.Should().Be(original);
    }
    
    [Fact]
    public void CalculateBidLevels_ValidInputs_ReturnsCorrectLevels()
    {
        // Arrange
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        var spreadBps = 10; // 0.1% total spread
        var levelSpacingBps = 5; // 0.05% between levels
        var numLevels = 10;
        
        // Act
        var bidLevels = PriceCalculator.CalculateBidLevels(
            midPriceBase, spreadBps, levelSpacingBps, numLevels);
        
        // Assert
        bidLevels.Length.Should().Be(numLevels);
        
        // First level (best bid) should be half spread below mid
        var bestBidDecimal = PriceCalculator.FromBaseUnits(bidLevels[0], 8);
        var expectedBestBid = midPrice * (1.0m - 0.0005m); // -5 bps (half of 10 bps)
        bestBidDecimal.Should().BeApproximately(expectedBestBid, 0.01m);
        
        // Each subsequent level should be further from mid
        for (int i = 1; i < bidLevels.Length; i++)
        {
            bidLevels[i].Should().BeLessThan(bidLevels[i - 1],
                $"Bid level {i} should be lower than level {i - 1}");
        }
    }
    
    [Fact]
    public void CalculateAskLevels_ValidInputs_ReturnsCorrectLevels()
    {
        // Arrange
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        var spreadBps = 10; // 0.1% total spread
        var levelSpacingBps = 5; // 0.05% between levels
        var numLevels = 10;
        
        // Act
        var askLevels = PriceCalculator.CalculateAskLevels(
            midPriceBase, spreadBps, levelSpacingBps, numLevels);
        
        // Assert
        askLevels.Length.Should().Be(numLevels);
        
        // First level (best ask) should be half spread above mid
        var bestAskDecimal = PriceCalculator.FromBaseUnits(askLevels[0], 8);
        var expectedBestAsk = midPrice * (1.0m + 0.0005m); // +5 bps (half of 10 bps)
        bestAskDecimal.Should().BeApproximately(expectedBestAsk, 0.01m);
        
        // Each subsequent level should be further from mid
        for (int i = 1; i < askLevels.Length; i++)
        {
            askLevels[i].Should().BeGreaterThan(askLevels[i - 1],
                $"Ask level {i} should be higher than level {i - 1}");
        }
    }
    
    [Fact]
    public void CalculateMargin_ValidInputs_ReturnsCorrectMargin()
    {
        // Arrange
        var price = 65000m;
        var quantity = 1m; // 1 BTC contract
        var priceBase = PriceCalculator.ToBaseUnits(price, 8);
        var quantityBase = PriceCalculator.ToBaseUnits(quantity, 8);
        var marginFactor = 1_200_000UL; // 1.2x = 120% margin
        var tradingDecimals = 8;
        var settlementDecimals = 6; // USDC has 6 decimals
        
        // Act
        var marginBase = PriceCalculator.CalculateMargin(
            priceBase, quantityBase, marginFactor, tradingDecimals, settlementDecimals);
        
        // Assert
        var marginDecimal = PriceCalculator.FromBaseUnits(marginBase, settlementDecimals);
        var expectedMargin = price * quantity * 1.2m; // 65000 * 1 * 1.2 = 78000 USDC
        marginDecimal.Should().BeApproximately(expectedMargin, 1m);
    }
    
    [Fact]
    public void CalculateTotalCapitalRequired_FullLadder_ReturnsCorrectTotal()
    {
        // Arrange
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        var spreadBps = 10;
        var levelSpacingBps = 5;
        var numLevels = 3; // Simplified for testing
        
        var bidPrices = PriceCalculator.CalculateBidLevels(
            midPriceBase, spreadBps, levelSpacingBps, numLevels);
        var askPrices = PriceCalculator.CalculateAskLevels(
            midPriceBase, spreadBps, levelSpacingBps, numLevels);
        
        // 100 contracts at each level for simplicity
        var quantities = new ulong[numLevels];
        for (int i = 0; i < numLevels; i++)
        {
            quantities[i] = PriceCalculator.ToBaseUnits(100m, 8);
        }
        
        var marginFactor = 1_200_000UL;
        var tradingDecimals = 8;
        var settlementDecimals = 6;
        
        // Act
        var totalMargin = PriceCalculator.CalculateTotalCapitalRequired(
            bidPrices, askPrices, quantities, marginFactor, tradingDecimals, settlementDecimals);
        
        // Assert
        totalMargin.Should().BeGreaterThan(0);
        
        // Should be approximately: 6 orders * 100 contracts * ~65000 * 1.2
        var totalMarginDecimal = PriceCalculator.FromBaseUnits(totalMargin, settlementDecimals);
        totalMarginDecimal.Should().BeGreaterThan(40_000_000m); // At least 40M USDC
    }
    
    [Theory]
    [InlineData(65000, 65000, 1, false)] // No change
    [InlineData(65000, 65006, 1, false)] // 0.92 bps change, within 1 bps tolerance
    [InlineData(65000, 65010, 1, true)]  // 1.54 bps change, exceeds 1 bps tolerance
    [InlineData(65000, 65650, 100, false)] // 1% change, within 100 bps tolerance
    [InlineData(65000, 66300, 100, true)]  // 2% change, exceeds 100 bps tolerance
    public void HasPriceMoved_VariousScenarios_ReturnsExpected(
        decimal oldPrice,
        decimal newPrice,
        int toleranceBps,
        bool expectedMoved)
    {
        // Arrange
        var oldPriceBase = PriceCalculator.ToBaseUnits(oldPrice, 8);
        var newPriceBase = PriceCalculator.ToBaseUnits(newPrice, 8);
        
        // Act
        var result = PriceCalculator.HasPriceMoved(oldPriceBase, newPriceBase, toleranceBps);
        
        // Assert
        result.Should().Be(expectedMoved);
    }
    
    [Fact]
    public void HasPriceMoved_ZeroOldPrice_AlwaysReturnsTrue()
    {
        // Arrange
        var oldPrice = 0UL;
        var newPrice = PriceCalculator.ToBaseUnits(65000m, 8);
        
        // Act
        var result = PriceCalculator.HasPriceMoved(oldPrice, newPrice, 1);
        
        // Assert
        result.Should().BeTrue("should always replace when no previous price");
    }
    
    [Fact]
    public void SpreadCalculation_BidAndAskSymmetric_AroundMidPrice()
    {
        // Arrange
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        var spreadBps = 10;
        var levelSpacingBps = 5;
        
        // Act
        var bidLevels = PriceCalculator.CalculateBidLevels(midPriceBase, spreadBps, levelSpacingBps, 1);
        var askLevels = PriceCalculator.CalculateAskLevels(midPriceBase, spreadBps, levelSpacingBps, 1);
        
        var bestBid = PriceCalculator.FromBaseUnits(bidLevels[0], 8);
        var bestAsk = PriceCalculator.FromBaseUnits(askLevels[0], 8);
        
        // Assert
        var spreadSize = bestAsk - bestBid;
        var midSpread = (bestBid + bestAsk) / 2m;
        
        // Mid price should be roughly halfway between bid and ask
        midSpread.Should().BeApproximately(midPrice, 1m);
        
        // Spread should be roughly 10 bps of mid price
        var expectedSpread = midPrice * 0.001m; // 10 bps = 0.1% = 0.001
        spreadSize.Should().BeApproximately(expectedSpread, 1m);
    }
}

