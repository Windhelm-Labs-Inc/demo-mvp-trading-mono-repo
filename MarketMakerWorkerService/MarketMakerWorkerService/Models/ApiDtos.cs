using System.Text.Json.Serialization;

namespace MarketMakerWorkerService.Models;

// ===========================
// Common Types
// ===========================

public record EntityId(
    [property: JsonPropertyName("shard")] long Shard,
    [property: JsonPropertyName("realm")] long Realm,
    [property: JsonPropertyName("num")] long Num)
{
    // Static factory method to parse account ID strings (avoids JSON deserialization ambiguity)
    public static EntityId FromString(string accountId)
    {
        var parts = accountId.Split('.');
        if (parts.Length == 3)
        {
            return new EntityId(
                long.Parse(parts[0]),
                long.Parse(parts[1]),
                long.Parse(parts[2]));
        }
        return new EntityId(0, 0, 0);
    }
    
    public override string ToString() => $"{Shard}.{Realm}.{Num}";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractSide
{
    [JsonPropertyName("long")]
    Long,
    
    [JsonPropertyName("short")]
    Short
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrderStatus
{
    [JsonPropertyName("pending")]
    Pending,
    
    [JsonPropertyName("open")]
    Open,
    
    [JsonPropertyName("filled")]
    Filled,
    
    [JsonPropertyName("partially_filled")]
    PartiallyFilled,
    
    [JsonPropertyName("cancelled")]
    Cancelled,
    
    [JsonPropertyName("expired")]
    Expired
}

// ===========================
// Authentication DTOs
// ===========================

/// <summary>
/// Request to get authentication challenge
/// </summary>
public record AuthChallengeRequest(
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("owner_type")] string OwnerType = "hapi"); // MUST be lowercase "hapi"

/// <summary>
/// Response from authentication challenge
/// </summary>
public record AuthChallengeResponse(
    [property: JsonPropertyName("challenge_id")] string ChallengeId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("expires_at_utc")] DateTime ExpiresAtUtc);

/// <summary>
/// Request to verify authentication challenge with signature
/// </summary>
public record AuthVerifyRequest(
    [property: JsonPropertyName("challenge_id")] string ChallengeId,
    [property: JsonPropertyName("signature_map")] string SignatureMap); // Base64-encoded protobuf SignatureMap

/// <summary>
/// Response from authentication verification
/// </summary>
public record AuthVerifyResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

// ===========================
// Market Data DTOs
// ===========================

/// <summary>
/// Market information response
/// </summary>
public record MarketInfoResponse(
    [property: JsonPropertyName("chain_id")] uint ChainId,
    [property: JsonPropertyName("ledger_id")] string LedgerId,
    [property: JsonPropertyName("trading_pair")] string TradingPair,
    [property: JsonPropertyName("settlement_token")] EntityId SettlementToken,
    [property: JsonPropertyName("trading_decimals")] uint TradingDecimals,
    [property: JsonPropertyName("settlement_decimals")] uint SettlementDecimals);

/// <summary>
/// Market spread response (best bid/ask)
/// </summary>
public record SpreadResponse(
    [property: JsonPropertyName("best_bid")] ulong BestBid,
    [property: JsonPropertyName("best_ask")] ulong BestAsk,
    [property: JsonPropertyName("bid_count")] int BidCount,
    [property: JsonPropertyName("ask_count")] int AskCount);

// ===========================
// Order Management DTOs
// ===========================

/// <summary>
/// Submit limit order request
/// </summary>
public record SubmitLimitOrderRequest(
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("owner_type")] string OwnerType, // "hapi" (lowercase)
    [property: JsonPropertyName("side")] string Side, // "long" or "short" (lowercase)
    [property: JsonPropertyName("price")] ulong Price,
    [property: JsonPropertyName("quantity")] ulong Quantity,
    [property: JsonPropertyName("margin")] ulong Margin); // Margin is a FACTOR (e.g., 1.2x = 1_200_000)

/// <summary>
/// Submit limit order response
/// </summary>
public record SubmitOrderResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("quantity_filled")] ulong QuantityFilled,
    [property: JsonPropertyName("order_status")] OrderStatus OrderStatus,
    [property: JsonPropertyName("trade_id")] Guid? TradeId,
    [property: JsonPropertyName("position_ids")] Guid[] PositionIds);

/// <summary>
/// Cancel order request
/// </summary>
public record CancelOrderRequest(
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("owner_type")] string OwnerType, // "hapi" (lowercase)
    [property: JsonPropertyName("order_id")] Guid OrderId);

/// <summary>
/// Cancel order response
/// </summary>
public record CancelOrderResponse(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("unfilled_quantity")] ulong UnfilledQuantity);

// ===========================
// Account DTOs
// ===========================

/// <summary>
/// Get balance request
/// </summary>
public record GetBalanceRequest(
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("owner_type")] string OwnerType); // "hapi" (lowercase)

/// <summary>
/// Balance response
/// </summary>
public record BalanceResponse(
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("balance")] ulong Balance); // Balance in settlement token base units

/// <summary>
/// Get account request
/// </summary>
public record GetAccountRequest(
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("owner_type")] string OwnerType); // "hapi" (lowercase)

/// <summary>
/// Account response (includes balance, orders, positions)
/// </summary>
public record AccountResponse(
    [property: JsonPropertyName("owner_id")] string OwnerId,
    [property: JsonPropertyName("balance")] ulong Balance,
    [property: JsonPropertyName("orders")] OrderInfo[] Orders,
    [property: JsonPropertyName("positions")] PositionInfo[] Positions);

/// <summary>
/// Order information in account response
/// </summary>
public record OrderInfo(
    [property: JsonPropertyName("order_id")] Guid OrderId,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("price")] ulong Price,
    [property: JsonPropertyName("quantity")] ulong Quantity,
    [property: JsonPropertyName("filled_quantity")] ulong FilledQuantity,
    [property: JsonPropertyName("status")] OrderStatus Status);

/// <summary>
/// Position information in account response
/// </summary>
public record PositionInfo(
    [property: JsonPropertyName("position_id")] Guid PositionId,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("quantity")] ulong Quantity,
    [property: JsonPropertyName("entry_price")] ulong EntryPrice);

// ===========================
// Error Response (RFC 7807 Problem Details)
// ===========================

public record ProblemDetails(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("instance")] string Instance);

