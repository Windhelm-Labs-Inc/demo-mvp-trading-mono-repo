using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;

namespace MarketMakerWorkerService.Services;

public interface IAccountService
{
    Task<BalanceResponse> GetBalanceAsync(string jwtToken, CancellationToken cancellationToken);
    Task<AccountResponse> GetAccountAsync(string jwtToken, CancellationToken cancellationToken);
    Task<AccountSnapshot> GetAccountSnapshotAsync(string jwtToken, CancellationToken cancellationToken);
}

public class AccountService : IAccountService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<AccountService> _logger;
    
    public AccountService(
        IHttpClientFactory httpClientFactory,
        IOptions<MarketMakerConfiguration> config,
        ILogger<AccountService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
        _logger = logger;
    }
    
    public async Task<BalanceResponse> GetBalanceAsync(
        string jwtToken,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching balance for account: {AccountId}", _config.AccountId);
        
        // API uses GET with query parameters, not POST with body
        var endpoint = $"/api/v1/account/balance?accountId={_config.AccountId}&ownerType=Hapi";
        
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint);
        requestMessage.Headers.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);
        
        var httpResponse = await client.SendAsync(requestMessage, cancellationToken);
        var responseContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        
        _logger.LogTrace("API Response from {Endpoint}: Status={StatusCode}, Body={Body}",
            endpoint, (int)httpResponse.StatusCode, responseContent);
        
        if (!httpResponse.IsSuccessStatusCode)
        {
            _logger.LogError(
                "API request failed: GET {Endpoint} returned {StatusCode}: {Response}",
                endpoint, httpResponse.StatusCode, responseContent);
            
            throw new HttpRequestException(
                $"API request failed: {httpResponse.StatusCode} - {responseContent}");
        }
        
        var response = System.Text.Json.JsonSerializer.Deserialize<BalanceResponse>(responseContent, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Failed to deserialize response from {endpoint}");
        
        _logger.LogInformation(
            "Balance fetched: {Balance} (settlement token base units)",
            response.Balance);
        
        return response;
    }
    
    public async Task<AccountResponse> GetAccountAsync(
        string jwtToken,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching account details: {AccountId}", _config.AccountId);
        
        // API uses GET with query parameters, not POST with body
        var endpoint = $"/api/v1/account?accountId={_config.AccountId}&ownerType=Hapi";
        
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, endpoint);
        requestMessage.Headers.Authorization = 
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);
        
        var httpResponse = await client.SendAsync(requestMessage, cancellationToken);
        var responseContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        
        _logger.LogTrace("API Response from {Endpoint}: Status={StatusCode}, Body={Body}",
            endpoint, (int)httpResponse.StatusCode, responseContent);
        
        if (!httpResponse.IsSuccessStatusCode)
        {
            _logger.LogError(
                "API request failed: GET {Endpoint} returned {StatusCode}: {Response}",
                endpoint, httpResponse.StatusCode, responseContent);
            
            throw new HttpRequestException(
                $"API request failed: {httpResponse.StatusCode} - {responseContent}");
        }
        
        var response = System.Text.Json.JsonSerializer.Deserialize<AccountResponse>(responseContent, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Failed to deserialize response from {endpoint}");
        
        _logger.LogInformation(
            "Account fetched: Balance={Balance}, Orders={OrderCount}, Positions={PositionCount}",
            response.Balance, response.Orders.Length, response.Positions.Length);
        
        return response;
    }
    
    public async Task<AccountSnapshot> GetAccountSnapshotAsync(
        string jwtToken,
        CancellationToken cancellationToken)
    {
        var accountResponse = await GetAccountAsync(jwtToken, cancellationToken);
        
        var snapshot = new AccountSnapshot
        {
            Balance = accountResponse.Balance,
            Orders = accountResponse.Orders,
            Positions = accountResponse.Positions,
            Timestamp = DateTime.UtcNow
        };
        
        // Log position details for debugging
        _logger.LogDebug("Account has {PositionCount} positions", accountResponse.Positions.Length);
        foreach (var pos in accountResponse.Positions)
        {
            _logger.LogDebug("Position: ID={PositionId}, Side={Side}, Qty={Quantity}, EntryPrice={EntryPrice}",
                pos.PositionId, pos.Side ?? "(null)", pos.Quantity, pos.EntryPrice);
        }
        
        _logger.LogDebug(
            "Account snapshot: Balance={Balance}, Positions={PositionCount}",
            snapshot.Balance, snapshot.Positions.Length);
        
        return snapshot;
    }
    
    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
        HttpMethod method,
        string endpoint,
        TRequest request,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        
        var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        });
        
        _logger.LogTrace("API Request to {Endpoint}: {Request}", endpoint, requestJson);
        
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var requestMessage = new HttpRequestMessage(method, endpoint)
        {
            Content = content
        };
        
        requestMessage.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);
        
        var response = await client.SendAsync(requestMessage, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        
        _logger.LogTrace("API Response from {Endpoint}: Status={StatusCode}, Body={Body}",
            endpoint, (int)response.StatusCode, responseContent);
        
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "API request failed: {Method} {Endpoint} returned {StatusCode}: {Response}",
                method, endpoint, response.StatusCode, responseContent);
            
            throw new HttpRequestException(
                $"API request failed: {response.StatusCode} - {responseContent}");
        }
        
        var result = JsonSerializer.Deserialize<TResponse>(responseContent, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        });
        
        return result ?? throw new InvalidOperationException(
            $"Failed to deserialize response from {endpoint}");
    }
}

