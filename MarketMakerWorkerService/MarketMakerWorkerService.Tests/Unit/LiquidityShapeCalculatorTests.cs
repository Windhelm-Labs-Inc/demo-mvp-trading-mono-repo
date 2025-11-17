using FluentAssertions;
using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Utilities;
using Xunit;

namespace MarketMakerWorkerService.Tests.Unit;

public class LiquidityShapeCalculatorTests
{
    [Fact]
    public void DefaultShape_HasCorrectSizes()
    {
        // Arrange & Act
        var shape = LiquidityShapeCalculator.DefaultShape;
        
        // Assert
        shape.Level0Size.Should().Be(100m);
        shape.Level1_2Size.Should().Be(50m);
        shape.Level3_9Size.Should().Be(10m);
        shape.TotalSize.Should().Be(270m); // 100 + 50 + 50 + (7 * 10)
    }
    
    [Theory]
    [InlineData(0, 100)]  // Level 0: 100 contracts
    [InlineData(1, 50)]   // Level 1: 50 contracts
    [InlineData(2, 50)]   // Level 2: 50 contracts
    [InlineData(3, 10)]   // Level 3: 10 contracts
    [InlineData(4, 10)]   // Level 4: 10 contracts
    [InlineData(5, 10)]   // Level 5: 10 contracts
    [InlineData(6, 10)]   // Level 6: 10 contracts
    [InlineData(7, 10)]   // Level 7: 10 contracts
    [InlineData(8, 10)]   // Level 8: 10 contracts
    [InlineData(9, 10)]   // Level 9: 10 contracts
    public void GetSizeForLevel_AllLevels_ReturnsCorrectSize(int level, decimal expectedSize)
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        
        // Act
        var size = shape.GetSizeForLevel(level);
        
        // Assert
        size.Should().Be(expectedSize);
    }
    
    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(100)]
    public void GetSizeForLevel_InvalidLevel_ThrowsException(int invalidLevel)
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() => shape.GetSizeForLevel(invalidLevel));
    }
    
    [Fact]
    public void CalculateQuantities_DefaultShape_ReturnsCorrectPattern()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var tradingDecimals = 8;
        var numLevels = 10;
        
        // Act
        var quantities = LiquidityShapeCalculator.CalculateQuantities(
            shape, tradingDecimals, numLevels);
        
        // Assert
        quantities.Length.Should().Be(10);
        
        // Convert back to decimal to verify pattern
        var qty0 = PriceCalculator.FromBaseUnits(quantities[0], tradingDecimals);
        var qty1 = PriceCalculator.FromBaseUnits(quantities[1], tradingDecimals);
        var qty2 = PriceCalculator.FromBaseUnits(quantities[2], tradingDecimals);
        var qty3 = PriceCalculator.FromBaseUnits(quantities[3], tradingDecimals);
        
        qty0.Should().Be(100m); // Level 0: 100
        qty1.Should().Be(50m);  // Level 1: 50
        qty2.Should().Be(50m);  // Level 2: 50
        qty3.Should().Be(10m);  // Level 3: 10
        
        // All subsequent levels should be 10
        for (int i = 3; i < 10; i++)
        {
            var qty = PriceCalculator.FromBaseUnits(quantities[i], tradingDecimals);
            qty.Should().Be(10m, $"Level {i} should be 10 contracts");
        }
    }
    
    [Theory]
    [InlineData(1.0, 100, 50, 10)]    // 100% scale
    [InlineData(0.5, 50, 25, 5)]      // 50% scale
    [InlineData(2.0, 200, 100, 20)]   // 200% scale
    [InlineData(0.1, 10, 5, 1)]       // 10% scale
    public void CalculateScaledQuantities_VariousScales_ScalesCorrectly(
        double scaleFactor,
        double expectedL0,
        double expectedL1,
        double expectedL3)
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var tradingDecimals = 8;
        
        // Act
        var quantities = LiquidityShapeCalculator.CalculateScaledQuantities(
            shape, tradingDecimals, (decimal)scaleFactor);
        
        // Assert
        var qty0 = PriceCalculator.FromBaseUnits(quantities[0], tradingDecimals);
        var qty1 = PriceCalculator.FromBaseUnits(quantities[1], tradingDecimals);
        var qty3 = PriceCalculator.FromBaseUnits(quantities[3], tradingDecimals);
        
        qty0.Should().BeApproximately((decimal)expectedL0, 0.01m);
        qty1.Should().BeApproximately((decimal)expectedL1, 0.01m);
        qty3.Should().BeApproximately((decimal)expectedL3, 0.01m);
    }
    
    [Fact]
    public void CalculateScaledQuantities_ZeroScale_ThrowsException()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            LiquidityShapeCalculator.CalculateScaledQuantities(shape, 8, 0m));
    }
    
    [Fact]
    public void CalculateScaledQuantities_NegativeScale_ThrowsException()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        
        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            LiquidityShapeCalculator.CalculateScaledQuantities(shape, 8, -0.5m));
    }
    
    [Fact]
    public void GetQuantityForLevel_SpecificLevel_ReturnsCorrectQuantity()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var tradingDecimals = 8;
        
        // Act
        var qty0 = LiquidityShapeCalculator.GetQuantityForLevel(0, shape, tradingDecimals);
        var qty1 = LiquidityShapeCalculator.GetQuantityForLevel(1, shape, tradingDecimals);
        var qty5 = LiquidityShapeCalculator.GetQuantityForLevel(5, shape, tradingDecimals);
        
        // Assert
        PriceCalculator.FromBaseUnits(qty0, tradingDecimals).Should().Be(100m);
        PriceCalculator.FromBaseUnits(qty1, tradingDecimals).Should().Be(50m);
        PriceCalculator.FromBaseUnits(qty5, tradingDecimals).Should().Be(10m);
    }
    
    [Fact]
    public void CalculateTotalQuantity_DefaultShape_Returns270()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var tradingDecimals = 8;
        
        // Act
        var totalBase = LiquidityShapeCalculator.CalculateTotalQuantity(
            shape, tradingDecimals);
        
        // Assert
        var totalDecimal = PriceCalculator.FromBaseUnits(totalBase, tradingDecimals);
        totalDecimal.Should().Be(270m); // 100 + 50 + 50 + (7 * 10)
    }
    
    [Fact]
    public void HasSufficientCapital_AdequateBalance_ReturnsTrue()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        
        var bidPrices = PriceCalculator.CalculateBidLevels(midPriceBase, 10, 5, 10);
        var askPrices = PriceCalculator.CalculateAskLevels(midPriceBase, 10, 5, 10);
        
        // Calculate actual capital needed, then provide more
        var quantities = LiquidityShapeCalculator.CalculateQuantities(shape, 8);
        var requiredMargin = PriceCalculator.CalculateTotalCapitalRequired(
            bidPrices, askPrices, quantities, 1_200_000UL, 8, 6);
        
        var availableBalance = (ulong)(requiredMargin * 2m); // Provide 2x what's needed
        
        // Act
        var result = LiquidityShapeCalculator.HasSufficientCapital(
            shape, bidPrices, askPrices, availableBalance,
            1_200_000UL, 8, 6, 0.8m);
        
        // Assert
        result.Should().BeTrue();
    }
    
    [Fact]
    public void HasSufficientCapital_InsufficientBalance_ReturnsFalse()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        
        var bidPrices = PriceCalculator.CalculateBidLevels(midPriceBase, 10, 5, 10);
        var askPrices = PriceCalculator.CalculateAskLevels(midPriceBase, 10, 5, 10);
        var availableBalance = PriceCalculator.ToBaseUnits(1000m, 6); // Only 1000 USDC
        var marginFactor = 1_200_000UL;
        
        // Act
        var result = LiquidityShapeCalculator.HasSufficientCapital(
            shape, bidPrices, askPrices, availableBalance,
            marginFactor, 8, 6, 0.8m);
        
        // Assert
        result.Should().BeFalse();
    }
    
    [Fact]
    public void CalculateMaxScaleFactor_AdequateBalance_Returns1()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        
        var bidPrices = PriceCalculator.CalculateBidLevels(midPriceBase, 10, 5, 10);
        var askPrices = PriceCalculator.CalculateAskLevels(midPriceBase, 10, 5, 10);
        
        // Calculate exact capital needed for full ladder
        var quantities = LiquidityShapeCalculator.CalculateQuantities(shape, 8);
        var fullCapital = PriceCalculator.CalculateTotalCapitalRequired(
            bidPrices, askPrices, quantities, 1_200_000UL, 8, 6);
        
        // Provide 2x the capital (accounting for 80% utilization)
        var availableBalance = (ulong)(fullCapital * 2m / 0.8m);
        
        // Act
        var scaleFactor = LiquidityShapeCalculator.CalculateMaxScaleFactor(
            shape, bidPrices, askPrices, availableBalance,
            1_200_000UL, 8, 6, 0.8m);
        
        // Assert
        scaleFactor.Should().Be(1.0m, "should not scale up even if excess capital");
    }
    
    [Fact]
    public void CalculateMaxScaleFactor_HalfBalance_ReturnsApproximatelyHalf()
    {
        // Arrange
        var shape = LiquidityShapeCalculator.DefaultShape;
        var midPrice = 65000m;
        var midPriceBase = PriceCalculator.ToBaseUnits(midPrice, 8);
        
        var bidPrices = PriceCalculator.CalculateBidLevels(midPriceBase, 10, 5, 10);
        var askPrices = PriceCalculator.CalculateAskLevels(midPriceBase, 10, 5, 10);
        
        // Calculate exact capital needed for full ladder
        var quantities = LiquidityShapeCalculator.CalculateQuantities(shape, 8);
        var fullCapital = PriceCalculator.CalculateTotalCapitalRequired(
            bidPrices, askPrices, quantities, 1_200_000UL, 8, 6);
        
        // Provide only half (with utilization factor)
        var halfBalance = fullCapital / 0.8m / 2m; // Divide by utilization, then by 2
        
        // Act
        var scaleFactor = LiquidityShapeCalculator.CalculateMaxScaleFactor(
            shape, bidPrices, askPrices, (ulong)halfBalance,
            1_200_000UL, 8, 6, 0.8m);
        
        // Assert
        scaleFactor.Should().BeApproximately(0.5m, 0.01m);
    }
    
    [Fact]
    public void CustomShape_CanBeCreated()
    {
        // Arrange & Act
        var customShape = new LiquidityShape
        {
            Level0Size = 200m,  // Double the default
            Level1_2Size = 100m,
            Level3_9Size = 20m
        };
        
        // Assert
        customShape.Level0Size.Should().Be(200m);
        customShape.GetSizeForLevel(0).Should().Be(200m);
        customShape.GetSizeForLevel(5).Should().Be(20m);
        customShape.TotalSize.Should().Be(540m); // 200 + 100 + 100 + (7 * 20)
    }
}

