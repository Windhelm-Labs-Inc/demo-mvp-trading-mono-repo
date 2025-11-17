using Microsoft.Extensions.Options;
using StackExchange.Redis;
using MarketMakerWorkerService.Configuration;

namespace MarketMakerWorkerService.Services;

public interface IRedisConnectionService
{
    IConnectionMultiplexer Connection { get; }
    Task<bool> TestConnectionAsync();
}

public class RedisConnectionService : IRedisConnectionService, IDisposable
{
    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisConnectionService> _logger;
    
    public IConnectionMultiplexer Connection => _connection;
    
    public RedisConnectionService(
        IOptions<MarketMakerConfiguration> config,
        ILogger<RedisConnectionService> logger)
    {
        _logger = logger;
        
        var connectionString = config.Value.RedisConnectionString;
        
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Redis connection string not configured. Ensure REDIS_CONNECTION_STRING environment variable is set.");
        }
        
        _logger.LogInformation("Connecting to Redis: {ConnectionString}", 
            MaskConnectionString(connectionString));
        
        try
        {
            _connection = ConnectionMultiplexer.Connect(connectionString);
            _logger.LogInformation("Successfully connected to Redis: {Endpoints}", 
                string.Join(", ", _connection.GetEndPoints().Select(e => e.ToString())));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Redis");
            throw;
        }
    }
    
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            // 🚨 READ-ONLY: Test connection by reading the production index key
            // We NEVER write to Redis, even for connection tests
            var db = _connection.GetDatabase();
            var indexKey = "spotindex:BTC_USD"; // Production index key
            
            var value = await db.StringGetAsync(indexKey);
            
            if (!value.HasValue)
            {
                _logger.LogWarning("Redis connection test: production index key '{IndexKey}' has no value (indexer may not be running)", indexKey);
                return false;
            }
            
            // Validate the value is parseable as JSON with IndexPrice field
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(value.ToString());
                var root = doc.RootElement;
                
                if (root.TryGetProperty("IndexPrice", out var indexPriceElement))
                {
                    var price = indexPriceElement.GetDecimal();
                    _logger.LogInformation("Redis connection test successful: read production index ${Price:F2} from key '{IndexKey}'", price, indexKey);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Redis connection test: IndexPrice field not found in JSON");
                    return false;
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Redis connection test: production index value is not valid JSON: {Value}", value.ToString());
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis connection test failed");
            return false;
        }
    }
    
    private static string MaskConnectionString(string connectionString)
    {
        // Mask password in connection string for logging
        if (connectionString.Contains("password=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = connectionString.Split(',');
            var maskedParts = parts.Select(part =>
            {
                if (part.Trim().StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                {
                    return "password=***";
                }
                return part;
            });
            return string.Join(",", maskedParts);
        }
        return connectionString;
    }
    
    public void Dispose()
    {
        _connection?.Dispose();
    }
}

