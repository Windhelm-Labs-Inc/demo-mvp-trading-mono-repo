using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Utilities;

namespace MarketMakerWorkerService.Services;

/// <summary>
/// Manages the state of all orders in the liquidity ladder
/// Tracks which orders are at which levels, their prices and quantities
/// Thread-safe for concurrent access
/// </summary>
public class OrderStateManager
{
    private readonly object _lock = new();
    private readonly OrderLevel[] _bidLevels;
    private readonly OrderLevel[] _askLevels;
    private readonly int _numLevels;
    
    private readonly ILogger<OrderStateManager> _logger;
    
    public OrderStateManager(
        ILogger<OrderStateManager> logger,
        int numLevels = 10)
    {
        _logger = logger;
        _numLevels = numLevels;
        _bidLevels = new OrderLevel[numLevels];
        _askLevels = new OrderLevel[numLevels];
    }
    
    /// <summary>
    /// Initialize the ladder with the specified number of levels
    /// Creates empty order levels
    /// </summary>
    public void InitializeLadder(int numLevels)
    {
        lock (_lock)
        {
            for (int i = 0; i < numLevels && i < _numLevels; i++)
            {
                _bidLevels[i] = new OrderLevel
                {
                    LevelIndex = i,
                    CurrentOrderId = null,
                    CurrentPrice = 0,
                    CurrentQuantity = 0,
                    LastUpdated = DateTime.UtcNow
                };
                
                _askLevels[i] = new OrderLevel
                {
                    LevelIndex = i,
                    CurrentOrderId = null,
                    CurrentPrice = 0,
                    CurrentQuantity = 0,
                    LastUpdated = DateTime.UtcNow
                };
            }
            
            _logger.LogInformation("Initialized ladder with {NumLevels} levels per side", numLevels);
        }
    }
    
    /// <summary>
    /// Update a bid level with new order information
    /// </summary>
    public void UpdateLevel(
        ContractSide side,
        int levelIndex,
        Guid orderId,
        ulong price,
        ulong quantity)
    {
        lock (_lock)
        {
            var levels = side == ContractSide.Long ? _bidLevels : _askLevels;
            
            if (levelIndex < 0 || levelIndex >= levels.Length)
            {
                _logger.LogWarning("Invalid level index: {LevelIndex}", levelIndex);
                return;
            }
            
            levels[levelIndex] = new OrderLevel
            {
                LevelIndex = levelIndex,
                CurrentOrderId = orderId,
                CurrentPrice = price,
                CurrentQuantity = quantity,
                LastUpdated = DateTime.UtcNow
            };
            
            _logger.LogDebug(
                "Updated {Side} level {LevelIndex}: OrderId={OrderId}, Price={Price}, Qty={Quantity}",
                side, levelIndex, orderId, price, quantity);
        }
    }
    
    /// <summary>
    /// Clear a level (remove order information)
    /// </summary>
    public void ClearLevel(ContractSide side, int levelIndex)
    {
        lock (_lock)
        {
            var levels = side == ContractSide.Long ? _bidLevels : _askLevels;
            
            if (levelIndex < 0 || levelIndex >= levels.Length)
                return;
            
            levels[levelIndex] = new OrderLevel
            {
                LevelIndex = levelIndex,
                CurrentOrderId = null,
                CurrentPrice = 0,
                CurrentQuantity = 0,
                LastUpdated = DateTime.UtcNow
            };
            
            _logger.LogDebug("Cleared {Side} level {LevelIndex}", side, levelIndex);
        }
    }
    
    /// <summary>
    /// Get current state of a bid level
    /// </summary>
    public OrderLevel GetBidLevel(int levelIndex)
    {
        lock (_lock)
        {
            if (levelIndex < 0 || levelIndex >= _bidLevels.Length)
                throw new ArgumentOutOfRangeException(nameof(levelIndex));
            
            return _bidLevels[levelIndex];
        }
    }
    
    /// <summary>
    /// Get current state of an ask level
    /// </summary>
    public OrderLevel GetAskLevel(int levelIndex)
    {
        lock (_lock)
        {
            if (levelIndex < 0 || levelIndex >= _askLevels.Length)
                throw new ArgumentOutOfRangeException(nameof(levelIndex));
            
            return _askLevels[levelIndex];
        }
    }
    
    /// <summary>
    /// Get all bid levels
    /// </summary>
    public OrderLevel[] GetAllBidLevels()
    {
        lock (_lock)
        {
            return _bidLevels.ToArray(); // Return a copy
        }
    }
    
    /// <summary>
    /// Get all ask levels
    /// </summary>
    public OrderLevel[] GetAllAskLevels()
    {
        lock (_lock)
        {
            return _askLevels.ToArray(); // Return a copy
        }
    }
    
    /// <summary>
    /// Get all active order IDs (orders currently in the book)
    /// </summary>
    public Guid[] GetAllActiveOrderIds()
    {
        lock (_lock)
        {
            var orderIds = new List<Guid>();
            
            foreach (var level in _bidLevels)
            {
                if (level?.CurrentOrderId.HasValue == true)
                    orderIds.Add(level.CurrentOrderId.Value);
            }
            
            foreach (var level in _askLevels)
            {
                if (level?.CurrentOrderId.HasValue == true)
                    orderIds.Add(level.CurrentOrderId.Value);
            }
            
            return orderIds.ToArray();
        }
    }
    
    /// <summary>
    /// Find which level contains a specific order ID
    /// </summary>
    public (ContractSide? Side, int? LevelIndex) FindOrderLevel(Guid orderId)
    {
        lock (_lock)
        {
            // Check bids
            for (int i = 0; i < _bidLevels.Length; i++)
            {
                if (_bidLevels[i]?.CurrentOrderId == orderId)
                    return (ContractSide.Long, i);
            }
            
            // Check asks
            for (int i = 0; i < _askLevels.Length; i++)
            {
                if (_askLevels[i]?.CurrentOrderId == orderId)
                    return (ContractSide.Short, i);
            }
            
            return (null, null);
        }
    }
    
    /// <summary>
    /// Count how many levels have active orders
    /// </summary>
    public (int BidCount, int AskCount) GetActiveLevelCounts()
    {
        lock (_lock)
        {
            int bidCount = _bidLevels.Count(l => l?.CurrentOrderId.HasValue == true);
            int askCount = _askLevels.Count(l => l?.CurrentOrderId.HasValue == true);
            
            return (bidCount, askCount);
        }
    }
    
    /// <summary>
    /// Check if a level has an active order
    /// </summary>
    public bool HasActiveOrder(ContractSide side, int levelIndex)
    {
        lock (_lock)
        {
            var levels = side == ContractSide.Long ? _bidLevels : _askLevels;
            
            if (levelIndex < 0 || levelIndex >= levels.Length)
                return false;
            
            return levels[levelIndex]?.CurrentOrderId.HasValue == true;
        }
    }
    
    /// <summary>
    /// Clear all levels (cancel all tracked orders)
    /// </summary>
    public void ClearAllLevels()
    {
        lock (_lock)
        {
            for (int i = 0; i < _numLevels; i++)
            {
                ClearLevel(ContractSide.Long, i);
                ClearLevel(ContractSide.Short, i);
            }
            
            _logger.LogInformation("Cleared all ladder levels");
        }
    }
    
    /// <summary>
    /// Get a snapshot of the current ladder state
    /// </summary>
    public LiquidityLadder GetLadderSnapshot(ulong midPrice)
    {
        lock (_lock)
        {
            return new LiquidityLadder
            {
                MidPrice = midPrice,
                BidLevels = _bidLevels.ToArray(),
                AskLevels = _askLevels.ToArray(),
                CreatedAt = DateTime.UtcNow
            };
        }
    }
    
    /// <summary>
    /// Calculate which orders need to be replaced given new price levels
    /// Returns list of replacements needed (minimal changes strategy)
    /// </summary>
    public List<OrderReplacement> CalculateReplacements(
        ulong[] newBidPrices,
        ulong[] newAskPrices,
        ulong[] newQuantities)
    {
        lock (_lock)
        {
            var replacements = new List<OrderReplacement>();
            
            // Check bid levels
            for (int i = 0; i < newBidPrices.Length && i < _bidLevels.Length; i++)
            {
                var level = _bidLevels[i];
                var newPrice = newBidPrices[i];
                var newQty = i < newQuantities.Length ? newQuantities[i] : 0;
                
                replacements.Add(new OrderReplacement
                {
                    LevelIndex = i,
                    Side = ContractSide.Long,
                    OldOrderId = level?.CurrentOrderId,
                    NewPrice = newPrice,
                    NewQuantity = newQty
                });
            }
            
            // Check ask levels
            for (int i = 0; i < newAskPrices.Length && i < _askLevels.Length; i++)
            {
                var level = _askLevels[i];
                var newPrice = newAskPrices[i];
                var newQty = i < newQuantities.Length ? newQuantities[i] : 0;
                
                replacements.Add(new OrderReplacement
                {
                    LevelIndex = i,
                    Side = ContractSide.Short,
                    OldOrderId = level?.CurrentOrderId,
                    NewPrice = newPrice,
                    NewQuantity = newQty
                });
            }
            
            _logger.LogDebug("Calculated {Count} order replacements needed", replacements.Count);
            
            return replacements;
        }
    }
}

