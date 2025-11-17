using System.Text;
using System.Text.Json;
using MarketMakerWorkerService.Models;

namespace MarketMakerWorkerService.Services;

public interface IMarketDataService
{
    Task<MarketInfoResponse> GetMarketInfoAsync(CancellationToken cancellationToken);
    Task<SpreadResponse> GetSpreadAsync(CancellationToken cancellationToken);
}

public class MarketDataService : IMarketDataService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MarketDataService> _logger;
    
    public MarketDataService(
        IHttpClientFactory httpClientFactory,
        ILogger<MarketDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }
    
    public async Task<MarketInfoResponse> GetMarketInfoAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching market info");
        
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        var response = await client.GetAsync("/api/v1/market/info", cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("Market info response: {Content}", content);
        
        var marketInfo = JsonSerializer.Deserialize<MarketInfoResponse>(content, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        });
        
        if (marketInfo == null)
        {
            throw new InvalidOperationException("Failed to deserialize market info");
        }
        
        _logger.LogInformation(
            "Market info loaded: {TradingPair}, TradingDecimals={TradingDecimals}, SettlementDecimals={SettlementDecimals}",
            marketInfo.TradingPair, marketInfo.TradingDecimals, marketInfo.SettlementDecimals);
        
        return marketInfo;
    }
    
    public async Task<SpreadResponse> GetSpreadAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching current spread");
        
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        var response = await client.GetAsync("/api/v1/market/spread", cancellationToken);
        
        response.EnsureSuccessStatusCode();
        
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        
        var spread = JsonSerializer.Deserialize<SpreadResponse>(content, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        });
        
        if (spread == null)
        {
            throw new InvalidOperationException("Failed to deserialize spread");
        }
        
        _logger.LogDebug(
            "Current spread: BestBid={BestBid}, BestAsk={BestAsk}",
            spread.BestBid, spread.BestAsk);
        
        return spread;
    }
}

