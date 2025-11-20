using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Models;

namespace MarketMakerWorkerService.Services;

public interface IOrderService
{
    Task<SubmitOrderResponse> SubmitLimitOrderAsync(
        ContractSide side,
        ulong price,
        ulong quantity,
        ulong marginFactor,
        string clientOrderId,
        string jwtToken,
        CancellationToken cancellationToken);
    
    Task<CancelOrderResponse> CancelOrderAsync(
        Guid orderId,
        string jwtToken,
        CancellationToken cancellationToken);
}

public class OrderService : IOrderService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MarketMakerConfiguration _config;
    private readonly ILogger<OrderService> _logger;
    
    public OrderService(
        IHttpClientFactory httpClientFactory,
        IOptions<MarketMakerConfiguration> config,
        ILogger<OrderService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config.Value;
        _logger = logger;
    }
    
    public async Task<SubmitOrderResponse> SubmitLimitOrderAsync(
        ContractSide side,
        ulong price,
        ulong quantity,
        ulong marginFactor,
        string clientOrderId,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Submitting limit order: Side={Side}, Price={Price}, Qty={Quantity}, Margin={Margin}, ClientOrderId={ClientOrderId}",
            side, price, quantity, marginFactor, clientOrderId);
        
        // Log detailed request for debugging margin issues
        _logger.LogDebug(
            "ORDER SUBMIT REQUEST: Side={Side}, Price={Price} base units, Qty={Qty} base units, Margin={Margin} base units",
            side == ContractSide.Long ? "long" : "short", price, quantity, marginFactor);
        
        var request = new SubmitLimitOrderRequest(
            ClientOrderId: clientOrderId,
            Kind: "limit",
            Margin: marginFactor,
            Account: new OrderAccountInfo(
                AccountId: _config.AccountId,
                OwnerType: "Hapi"), // Match AccountService casing
            Price: price,
            Quantity: quantity,
            Side: side == ContractSide.Long ? "long" : "short", // Must be lowercase
            TimeInForce: "good_until_filled"); // API expects "good_until_filled" not "gtc"
        
        var response = await SendRequestAsync<SubmitLimitOrderRequest, SubmitOrderResponse>(
            HttpMethod.Post,
            "/api/v1/order/submit",
            request,
            jwtToken,
            cancellationToken);
        
        _logger.LogDebug(
            "Order submitted: OrderId={OrderId}, Status={Status}, Filled={Filled}",
            response.OrderId, response.OrderStatus, response.QuantityFilled);
        
        return response;
    }
    
    public async Task<CancelOrderResponse> CancelOrderAsync(
        Guid orderId,
        string jwtToken,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Cancelling order: OrderId={OrderId}", orderId);
        
        // Cancel endpoint uses query parameter with DELETE method
        var endpoint = $"/api/v1/order/cancel?orderId={orderId}";
        
        var client = _httpClientFactory.CreateClient("PerpetualsAPI");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        requestMessage.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", jwtToken);
        
        // Add idempotency key
        requestMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        
        var httpResponse = await client.SendAsync(requestMessage, cancellationToken);
        
        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "API request failed: DELETE {Endpoint} returned {StatusCode}: {ErrorBody}",
                endpoint, httpResponse.StatusCode, errorBody);
            throw new HttpRequestException(
                $"API request failed: {httpResponse.StatusCode} - {errorBody}");
        }
        
        var responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        var response = JsonSerializer.Deserialize<CancelOrderResponse>(responseBody)
            ?? throw new InvalidOperationException("Failed to deserialize CancelOrderResponse");
        
        _logger.LogDebug(
            "Order cancelled: OrderId={OrderId}, UnfilledQty={UnfilledQuantity}",
            response.OrderId, response.UnfilledQuantity);
        
        return response;
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
        
        // Add idempotency key for order operations
        requestMessage.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        
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

