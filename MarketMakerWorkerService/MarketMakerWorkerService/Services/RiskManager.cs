using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Utilities;

namespace MarketMakerWorkerService.Services;

/// <summary>
/// Risk management system - enforces position limits and monitors risk exposure
/// </summary>
public class RiskManager
{
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<RiskManager> _logger;

    public RiskManager(
        IOptions<MarketMakerConfiguration> config,
        ILogger<RiskManager> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Check if we can place new order given current position and risk limits
    /// </summary>
    public bool CanPlaceOrder(
        ContractSide side,
        ulong quantity,
        ulong price,
        decimal currentPosition)
    {
        var quantityDecimal = PriceCalculator.FromBaseUnits(quantity, _config.TradingDecimals);
        var priceDecimal = PriceCalculator.FromBaseUnits(price, _config.TradingDecimals);

        // Calculate what new position would be if order fills
        var positionChange = side == ContractSide.Long ? quantityDecimal : -quantityDecimal;
        var projectedPosition = currentPosition + positionChange;

        // Check position limit
        if (Math.Abs(projectedPosition) > _config.MaxPositionSize)
        {
            _logger.LogWarning(
                "RISK LIMIT: Position limit would be breached. Current={Current}, Projected={Projected}, Limit={Limit}",
                currentPosition,
                projectedPosition,
                _config.MaxPositionSize);
            return false;
        }

        // Check notional exposure
        var notionalExposure = Math.Abs(projectedPosition) * priceDecimal;
        if (notionalExposure > _config.MaxNotionalExposure)
        {
            _logger.LogWarning(
                "RISK LIMIT: Notional exposure limit would be breached. Exposure=${Exposure:N2}, Limit=${Limit:N2}",
                notionalExposure,
                _config.MaxNotionalExposure);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Check if emergency shutdown is required
    /// </summary>
    public async Task<bool> ShouldEmergencyShutdownAsync(
        IAccountService accountService,
        string jwtToken,
        CancellationToken ct)
    {
        try
        {
            var balanceResponse = await accountService.GetBalanceAsync(jwtToken, ct);
            var balance = PriceCalculator.FromBaseUnits(
                balanceResponse.Balance,
                _config.SettlementDecimals);

            if (balance < _config.MinAccountBalance)
            {
                _logger.LogCritical(
                    "⚠️ EMERGENCY SHUTDOWN: Balance ${Balance:N2} below minimum ${MinBalance:N2}",
                    balance,
                    _config.MinAccountBalance);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check emergency shutdown conditions");
            return false;
        }
    }

    /// <summary>
    /// Calculate risk metrics for monitoring
    /// </summary>
    public RiskMetrics CalculateRiskMetrics(
        decimal currentPosition,
        decimal currentPrice,
        ulong availableBalance)
    {
        var notionalExposure = Math.Abs(currentPosition) * currentPrice;
        var balance = PriceCalculator.FromBaseUnits(availableBalance, _config.SettlementDecimals);
        
        var leverage = balance > 0 ? notionalExposure / balance : 0;
        var positionUtilization = _config.MaxPositionSize > 0 
            ? Math.Abs(currentPosition) / _config.MaxPositionSize 
            : 0;
        var exposureUtilization = _config.MaxNotionalExposure > 0
            ? notionalExposure / _config.MaxNotionalExposure
            : 0;

        return new RiskMetrics
        {
            CurrentPosition = currentPosition,
            NotionalExposure = notionalExposure,
            Leverage = leverage,
            PositionUtilization = positionUtilization,
            ExposureUtilization = exposureUtilization,
            AvailableBalance = balance,
            Timestamp = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Risk metrics snapshot
/// </summary>
public record RiskMetrics
{
    public decimal CurrentPosition { get; init; }
    public decimal NotionalExposure { get; init; }
    public decimal Leverage { get; init; }
    public decimal PositionUtilization { get; init; }
    public decimal ExposureUtilization { get; init; }
    public decimal AvailableBalance { get; init; }
    public DateTime Timestamp { get; init; }
}

