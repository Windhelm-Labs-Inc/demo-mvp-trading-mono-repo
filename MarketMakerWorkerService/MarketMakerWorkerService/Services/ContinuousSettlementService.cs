using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;

namespace MarketMakerWorkerService.Services;

public interface IContinuousSettlementService
{
    Task<SettlementResult> CheckAndSettlePositionsAsync(
        string jwtToken,
        CancellationToken cancellationToken);
}

public class ContinuousSettlementService : IContinuousSettlementService
{
    private readonly IAccountService _accountService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<ContinuousSettlementService> _logger;
    
    public ContinuousSettlementService(
        IAccountService accountService,
        IHttpClientFactory httpClientFactory,
        IOptions<MarketMakerConfiguration> config,
        ILogger<ContinuousSettlementService> logger)
    {
        _accountService = accountService;
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
        _logger = logger;
    }
    
    public async Task<SettlementResult> CheckAndSettlePositionsAsync(
        string jwtToken,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Checking for settleable positions");
        
        // Get account with positions
        var account = await _accountService.GetAccountAsync(jwtToken, cancellationToken);
        var positions = account.Positions;
        
        if (positions.Length == 0)
        {
            _logger.LogInformation("No positions to settle");
            return SettlementResult.NoPositions();
        }
        
        // Separate by side
        var longPositions = positions
            .Where(p => p.Side?.Equals("long", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        var shortPositions = positions
            .Where(p => p.Side?.Equals("short", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        
        var totalLongQty = longPositions.Sum(p => (decimal)p.Quantity);
        var totalShortQty = shortPositions.Sum(p => (decimal)p.Quantity);
        
        _logger.LogInformation(
            "Position summary: {LongCount} longs ({LongQty} qty), {ShortCount} shorts ({ShortQty} qty)",
            longPositions.Count, totalLongQty, shortPositions.Count, totalShortQty);
        
        // Check if we can settle
        if (longPositions.Count == 0 || shortPositions.Count == 0)
        {
            _logger.LogInformation("Cannot settle: Need both long and short positions");
            return SettlementResult.NoSettleable(totalLongQty, totalShortQty);
        }
        
        // Calculate max settleable (must be balanced)
        var maxSettleable = Math.Min(totalLongQty, totalShortQty);
        
        if (maxSettleable == 0)
        {
            _logger.LogInformation("No settleable quantity");
            return SettlementResult.NoSettleable(totalLongQty, totalShortQty);
        }
        
        // Build settlement request
        var settlementQuantities = BuildSettlementQuantities(
            longPositions, shortPositions, maxSettleable);
        
        // Verify balance
        var totalLongSettled = settlementQuantities
            .Where(s => longPositions.Any(p => p.PositionId == s.PositionId))
            .Sum(s => (decimal)s.Quantity);
        var totalShortSettled = settlementQuantities
            .Where(s => shortPositions.Any(p => p.PositionId == s.PositionId))
            .Sum(s => (decimal)s.Quantity);
        
        if (totalLongSettled != totalShortSettled)
        {
            _logger.LogError(
                "Settlement quantities not balanced! Long={Long}, Short={Short}",
                totalLongSettled, totalShortSettled);
            return SettlementResult.Failed("Unbalanced settlement quantities");
        }
        
        _logger.LogInformation(
            "Settling {Count} positions: {Qty} long + {Qty} short",
            settlementQuantities.Count, totalLongSettled, totalShortSettled);
        
        // Execute settlement
        try
        {
            var settlementId = await SubmitSettlementAsync(
                settlementQuantities, jwtToken, cancellationToken);
            
            _logger.LogInformation(
                "Settlement metrics: Positions={Count}, Quantity={Qty}, " +
                "LongRemaining={LongRem}, ShortRemaining={ShortRem}",
                settlementQuantities.Count,
                totalLongSettled,
                totalLongQty - totalLongSettled,
                totalShortQty - totalShortSettled);
            
            _logger.LogInformation("Settlement successful: {SettlementId}", settlementId);
            
            return SettlementResult.Settled(settlementId, totalLongSettled, settlementQuantities.Count);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("400"))
        {
            _logger.LogWarning("Settlement rejected by API (likely already settled): {Error}", ex.Message);
            return SettlementResult.Failed("Already settled or invalid");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Settlement API request failed");
            return SettlementResult.Failed($"API error: {ex.Message}");
        }
    }
    
    private List<SettlementQuantity> BuildSettlementQuantities(
        List<PositionInfo> longPositions,
        List<PositionInfo> shortPositions,
        decimal maxSettleable)
    {
        var result = new List<SettlementQuantity>();
        var remainingToSettle = maxSettleable;
        
        // Settle shorts first (complete positions if possible)
        foreach (var pos in shortPositions)
        {
            if (remainingToSettle == 0) break;
            
            var quantity = Math.Min((decimal)pos.Quantity, remainingToSettle);
            result.Add(new SettlementQuantity(pos.PositionId, (ulong)quantity));
            remainingToSettle -= quantity;
        }
        
        // Reset for longs
        remainingToSettle = maxSettleable;
        
        // Settle longs to match
        foreach (var pos in longPositions)
        {
            if (remainingToSettle == 0) break;
            
            var quantity = Math.Min((decimal)pos.Quantity, remainingToSettle);
            result.Add(new SettlementQuantity(pos.PositionId, (ulong)quantity));
            remainingToSettle -= quantity;
        }
        
        return result;
    }
    
    private async Task<string> SubmitSettlementAsync(
        List<SettlementQuantity> settlementQuantities,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        var endpoint = "/api/v1/position/settle";
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        
        var request = new SettlementRequest(settlementQuantities);
        
        var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = content
        };
        
        // Add idempotency key (required by API)
        var idempotencyKey = Guid.NewGuid().ToString();
        requestMessage.Headers.Add("Idempotency-Key", idempotencyKey);
        requestMessage.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);
        
        _logger.LogDebug("Settlement request with Idempotency-Key: {Key}", idempotencyKey);
        
        var response = await client.SendAsync(requestMessage, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Settlement failed: {StatusCode} - {Response}",
                response.StatusCode, responseContent);
            throw new HttpRequestException(
                $"Settlement failed: {response.StatusCode} - {responseContent}");
        }
        
        var result = JsonSerializer.Deserialize<SettlementResponse>(
            responseContent,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            });
        
        return result?.SettlementId ?? throw new InvalidOperationException(
            "Settlement response missing settlement_id");
    }
}

