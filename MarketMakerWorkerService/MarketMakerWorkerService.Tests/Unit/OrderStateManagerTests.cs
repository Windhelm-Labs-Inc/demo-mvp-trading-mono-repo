using FluentAssertions;
using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace MarketMakerWorkerService.Tests.Unit;

public class OrderStateManagerTests
{
    private readonly ILogger<OrderStateManager> _logger;
    
    public OrderStateManagerTests()
    {
        _logger = Mock.Of<ILogger<OrderStateManager>>();
    }
    
    [Fact]
    public void InitializeLadder_CreatesEmptyLevels()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        
        // Act
        manager.InitializeLadder(10);
        
        // Assert
        for (int i = 0; i < 10; i++)
        {
            var bidLevel = manager.GetBidLevel(i);
            bidLevel.CurrentOrderId.Should().BeNull();
            bidLevel.CurrentPrice.Should().Be(0);
            bidLevel.CurrentQuantity.Should().Be(0);
            
            var askLevel = manager.GetAskLevel(i);
            askLevel.CurrentOrderId.Should().BeNull();
            askLevel.CurrentPrice.Should().Be(0);
            askLevel.CurrentQuantity.Should().Be(0);
        }
    }
    
    [Fact]
    public void UpdateLevel_Bid_StoresOrderInformation()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        var orderId = Guid.NewGuid();
        var price = 6500000000000000UL;
        var quantity = 10000000000UL;
        
        // Act
        manager.UpdateLevel(ContractSide.Long, 0, orderId, price, quantity);
        
        // Assert
        var level = manager.GetBidLevel(0);
        level.CurrentOrderId.Should().Be(orderId);
        level.CurrentPrice.Should().Be(price);
        level.CurrentQuantity.Should().Be(quantity);
        level.LevelIndex.Should().Be(0);
    }
    
    [Fact]
    public void UpdateLevel_Ask_StoresOrderInformation()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        var orderId = Guid.NewGuid();
        var price = 6510000000000000UL;
        var quantity = 5000000000UL;
        
        // Act
        manager.UpdateLevel(ContractSide.Short, 2, orderId, price, quantity);
        
        // Assert
        var level = manager.GetAskLevel(2);
        level.CurrentOrderId.Should().Be(orderId);
        level.CurrentPrice.Should().Be(price);
        level.CurrentQuantity.Should().Be(quantity);
        level.LevelIndex.Should().Be(2);
    }
    
    [Fact]
    public void ClearLevel_RemovesOrderInformation()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        var orderId = Guid.NewGuid();
        manager.UpdateLevel(ContractSide.Long, 0, orderId, 6500000000000000UL, 10000000000UL);
        
        // Act
        manager.ClearLevel(ContractSide.Long, 0);
        
        // Assert
        var level = manager.GetBidLevel(0);
        level.CurrentOrderId.Should().BeNull();
        level.CurrentPrice.Should().Be(0);
        level.CurrentQuantity.Should().Be(0);
    }
    
    [Fact]
    public void GetAllBidLevels_ReturnsAllBidLevels()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        // Update a few levels
        manager.UpdateLevel(ContractSide.Long, 0, Guid.NewGuid(), 65000UL, 100UL);
        manager.UpdateLevel(ContractSide.Long, 1, Guid.NewGuid(), 64900UL, 50UL);
        
        // Act
        var levels = manager.GetAllBidLevels();
        
        // Assert
        levels.Length.Should().Be(10);
        levels[0].CurrentOrderId.Should().NotBeNull();
        levels[1].CurrentOrderId.Should().NotBeNull();
        levels[2].CurrentOrderId.Should().BeNull();
    }
    
    [Fact]
    public void GetAllAskLevels_ReturnsAllAskLevels()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        // Update a few levels
        manager.UpdateLevel(ContractSide.Short, 0, Guid.NewGuid(), 65100UL, 100UL);
        manager.UpdateLevel(ContractSide.Short, 3, Guid.NewGuid(), 65300UL, 10UL);
        
        // Act
        var levels = manager.GetAllAskLevels();
        
        // Assert
        levels.Length.Should().Be(10);
        levels[0].CurrentOrderId.Should().NotBeNull();
        levels[3].CurrentOrderId.Should().NotBeNull();
        levels[1].CurrentOrderId.Should().BeNull();
    }
    
    [Fact]
    public void GetAllActiveOrderIds_ReturnsOnlyActiveOrders()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        var orderId1 = Guid.NewGuid();
        var orderId2 = Guid.NewGuid();
        var orderId3 = Guid.NewGuid();
        
        manager.UpdateLevel(ContractSide.Long, 0, orderId1, 65000UL, 100UL);
        manager.UpdateLevel(ContractSide.Long, 1, orderId2, 64900UL, 50UL);
        manager.UpdateLevel(ContractSide.Short, 0, orderId3, 65100UL, 100UL);
        
        // Act
        var activeOrderIds = manager.GetAllActiveOrderIds();
        
        // Assert
        activeOrderIds.Length.Should().Be(3);
        activeOrderIds.Should().Contain(orderId1);
        activeOrderIds.Should().Contain(orderId2);
        activeOrderIds.Should().Contain(orderId3);
    }
    
    [Fact]
    public void FindOrderLevel_ExistingBidOrder_ReturnsCorrectLocation()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        var orderId = Guid.NewGuid();
        manager.UpdateLevel(ContractSide.Long, 5, orderId, 65000UL, 100UL);
        
        // Act
        var (side, levelIndex) = manager.FindOrderLevel(orderId);
        
        // Assert
        side.Should().Be(ContractSide.Long);
        levelIndex.Should().Be(5);
    }
    
    [Fact]
    public void FindOrderLevel_ExistingAskOrder_ReturnsCorrectLocation()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        var orderId = Guid.NewGuid();
        manager.UpdateLevel(ContractSide.Short, 7, orderId, 65100UL, 50UL);
        
        // Act
        var (side, levelIndex) = manager.FindOrderLevel(orderId);
        
        // Assert
        side.Should().Be(ContractSide.Short);
        levelIndex.Should().Be(7);
    }
    
    [Fact]
    public void FindOrderLevel_NonExistentOrder_ReturnsNull()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        var nonExistentOrderId = Guid.NewGuid();
        
        // Act
        var (side, levelIndex) = manager.FindOrderLevel(nonExistentOrderId);
        
        // Assert
        side.Should().BeNull();
        levelIndex.Should().BeNull();
    }
    
    [Fact]
    public void GetActiveLevelCounts_ReturnsCorrectCounts()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        // Add 3 bid orders and 5 ask orders
        for (int i = 0; i < 3; i++)
        {
            manager.UpdateLevel(ContractSide.Long, i, Guid.NewGuid(), 65000UL, 100UL);
        }
        for (int i = 0; i < 5; i++)
        {
            manager.UpdateLevel(ContractSide.Short, i, Guid.NewGuid(), 65100UL, 100UL);
        }
        
        // Act
        var (bidCount, askCount) = manager.GetActiveLevelCounts();
        
        // Assert
        bidCount.Should().Be(3);
        askCount.Should().Be(5);
    }
    
    [Fact]
    public void HasActiveOrder_WithActiveOrder_ReturnsTrue()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        manager.UpdateLevel(ContractSide.Long, 0, Guid.NewGuid(), 65000UL, 100UL);
        
        // Act
        var result = manager.HasActiveOrder(ContractSide.Long, 0);
        
        // Assert
        result.Should().BeTrue();
    }
    
    [Fact]
    public void HasActiveOrder_WithoutActiveOrder_ReturnsFalse()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        // Act
        var result = manager.HasActiveOrder(ContractSide.Long, 0);
        
        // Assert
        result.Should().BeFalse();
    }
    
    [Fact]
    public void ClearAllLevels_RemovesAllOrders()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        // Add orders to all levels
        for (int i = 0; i < 10; i++)
        {
            manager.UpdateLevel(ContractSide.Long, i, Guid.NewGuid(), 65000UL, 100UL);
            manager.UpdateLevel(ContractSide.Short, i, Guid.NewGuid(), 65100UL, 100UL);
        }
        
        // Act
        manager.ClearAllLevels();
        
        // Assert
        var activeOrderIds = manager.GetAllActiveOrderIds();
        activeOrderIds.Length.Should().Be(0);
        
        var (bidCount, askCount) = manager.GetActiveLevelCounts();
        bidCount.Should().Be(0);
        askCount.Should().Be(0);
    }
    
    [Fact]
    public void GetLadderSnapshot_ReturnsCurrentState()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        manager.UpdateLevel(ContractSide.Long, 0, Guid.NewGuid(), 64900UL, 100UL);
        manager.UpdateLevel(ContractSide.Short, 0, Guid.NewGuid(), 65100UL, 100UL);
        
        var midPrice = 65000UL;
        
        // Act
        var snapshot = manager.GetLadderSnapshot(midPrice);
        
        // Assert
        snapshot.MidPrice.Should().Be(midPrice);
        snapshot.BidLevels.Length.Should().Be(10);
        snapshot.AskLevels.Length.Should().Be(10);
        snapshot.BidLevels[0].CurrentOrderId.Should().NotBeNull();
        snapshot.AskLevels[0].CurrentOrderId.Should().NotBeNull();
    }
    
    [Fact]
    public void UpdateLevel_InvalidLevelIndex_DoesNotThrow()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        // Act & Assert
        manager.Invoking(m => m.UpdateLevel(ContractSide.Long, -1, Guid.NewGuid(), 65000UL, 100UL))
            .Should().NotThrow();
        
        manager.Invoking(m => m.UpdateLevel(ContractSide.Long, 20, Guid.NewGuid(), 65000UL, 100UL))
            .Should().NotThrow();
    }
    
    [Fact]
    public void ThreadSafety_ConcurrentUpdates_DoesNotThrow()
    {
        // Arrange
        var manager = new OrderStateManager(_logger);
        manager.InitializeLadder(10);
        
        // Act - Multiple threads updating different levels
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            int levelIndex = i;
            tasks.Add(Task.Run(() =>
            {
                manager.UpdateLevel(ContractSide.Long, levelIndex, Guid.NewGuid(), 65000UL, 100UL);
            }));
        }
        
        // Assert
        Action act = () => Task.WaitAll(tasks.ToArray());
        act.Should().NotThrow();
        
        var (bidCount, _) = manager.GetActiveLevelCounts();
        bidCount.Should().Be(10);
    }
}

