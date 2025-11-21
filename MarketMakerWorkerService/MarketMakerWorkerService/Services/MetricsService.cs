using System.Diagnostics.Metrics;

namespace MarketMakerWorkerService.Services;

/// <summary>
/// Service for recording OpenTelemetry metrics for the Market Maker
/// Tracks order operations, STP actions, and index updates
/// </summary>
public class MetricsService
{
    // Order placement metrics
    private readonly Counter<long> _ordersPlaced;
    private readonly Counter<long> _ordersCanceled;
    
    // Order failure metrics (separate by operation)
    private readonly Counter<long> _ordersSubmitFailed;
    private readonly Counter<long> _ordersCancelFailed;
    
    // STP metrics (separate by type)
    private readonly Counter<long> _stpBids;
    private readonly Counter<long> _stpAsks;
    private readonly Counter<long> _stpBothSides;
    
    // Index update metrics (separate by result)
    private readonly Counter<long> _indexUpdatesSuccess;
    private readonly Counter<long> _indexUpdatesFailure;
    
    public MetricsService(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("MarketMaker");
        
        // Order metrics
        _ordersPlaced = meter.CreateCounter<long>("orders.placed",
            description: "Number of successfully placed orders");
        _ordersCanceled = meter.CreateCounter<long>("orders.canceled",
            description: "Number of successfully canceled orders");
        
        // Failure metrics
        _ordersSubmitFailed = meter.CreateCounter<long>("orders.submit.failed",
            description: "Number of failed order submissions");
        _ordersCancelFailed = meter.CreateCounter<long>("orders.cancel.failed",
            description: "Number of failed order cancellations");
        
        // STP metrics
        _stpBids = meter.CreateCounter<long>("stp.bids",
            description: "STP actions triggered on bid side");
        _stpAsks = meter.CreateCounter<long>("stp.asks",
            description: "STP actions triggered on ask side");
        _stpBothSides = meter.CreateCounter<long>("stp.both_sides",
            description: "STP actions triggered on both sides");
        
        // Index update metrics
        _indexUpdatesSuccess = meter.CreateCounter<long>("index.updates.success",
            description: "Successful price index updates");
        _indexUpdatesFailure = meter.CreateCounter<long>("index.updates.failure",
            description: "Failed price index updates");
    }
    
    // Order placement
    public void RecordOrdersPlaced(int count) => _ordersPlaced.Add(count);
    public void RecordOrdersCanceled(int count) => _ordersCanceled.Add(count);
    
    // Order failures
    public void RecordOrdersSubmitFailed(int count) => _ordersSubmitFailed.Add(count);
    public void RecordOrdersCancelFailed(int count) => _ordersCancelFailed.Add(count);
    
    // STP actions
    public void RecordStpBids() => _stpBids.Add(1);
    public void RecordStpAsks() => _stpAsks.Add(1);
    public void RecordStpBothSides() => _stpBothSides.Add(1);
    
    // Index updates
    public void RecordIndexUpdateSuccess() => _indexUpdatesSuccess.Add(1);
    public void RecordIndexUpdateFailure() => _indexUpdatesFailure.Add(1);
}

