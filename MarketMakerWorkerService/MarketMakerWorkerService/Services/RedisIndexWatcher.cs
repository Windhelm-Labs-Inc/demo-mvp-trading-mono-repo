using System.Reactive.Linq;
using System.Text.Json;
using MarketMakerWorkerService.Models;
using StackExchange.Redis;

namespace MarketMakerWorkerService.Services;

/// <summary>
/// Watches Redis for index price changes and emits updates as an Observable stream.
/// 🚨 READ-ONLY: Only reads from production Redis, never writes.
/// Follows the pattern from RedisReactiveWatchTests.cs - polls and emits on change.
/// </summary>
public class RedisIndexWatcher
{
    private readonly IRedisConnectionService _redisConnection;
    private readonly ILogger<RedisIndexWatcher> _logger;

    public RedisIndexWatcher(
        IRedisConnectionService redisConnection,
        ILogger<RedisIndexWatcher> logger)
    {
        _redisConnection = redisConnection;
        _logger = logger;
    }

    /// <summary>
    /// Create an observable stream of index price updates from Redis (READ-ONLY)
    /// Polls Redis key and emits IndexPriceUpdate when value changes
    /// </summary>
    /// <param name="indexKey">Redis key to watch (e.g., "spotindex:BTC_USD")</param>
    /// <param name="pollIntervalMs">Polling interval in milliseconds (default: 50ms)</param>
    /// <param name="cancellationToken">Cancellation token to stop watching</param>
    /// <returns>Observable stream of price updates</returns>
    public IObservable<IndexPriceUpdate> CreatePriceObservable(
        string indexKey,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        return Observable.Create<IndexPriceUpdate>(async (observer, token) =>
        {
            var db = _redisConnection.Connection.GetDatabase();
            string? lastValue = null;

            _logger.LogInformation("Starting Redis price watcher for key: {Key})", 
                indexKey);
            

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // READ-ONLY: Only StringGetAsync, never StringSetAsync
                        var value = await db.StringGetAsync(indexKey);

                        if (value.HasValue)
                        {
                            var currentValue = value.ToString();

                            // Only emit if price changed
                            if (currentValue != lastValue)
                            {
                                try
                                {
                                    // Parse JSON to extract IndexPrice
                                    using var doc = JsonDocument.Parse(currentValue);
                                    var root = doc.RootElement;
                                    
                                    if (root.TryGetProperty("IndexPrice", out var indexPriceElement))
                                    {
                                        var price = indexPriceElement.GetDecimal();
                                        
                                        var update = new IndexPriceUpdate(
                                            Price: price,
                                            Timestamp: DateTime.UtcNow);

                                        _logger.LogInformation(
                                            "Index price updated: {Key} = ${Price:F2}",
                                            indexKey,
                                            price);

                                        observer.OnNext(update);
                                        lastValue = currentValue;
                                    }
                                    else
                                    {
                                        _logger.LogWarning("IndexPrice field not found in JSON: {Value}", currentValue);
                                    }
                                }
                                catch (JsonException ex)
                                {
                                    _logger.LogWarning(ex, "Failed to parse JSON value: {Value}", currentValue);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Redis key {Key} has no value (production indexer may not be running)", indexKey);
                        }
                    }
                    catch (RedisException ex)
                    {
                        _logger.LogError(ex, "Redis error reading key {Key}", indexKey);
                        // Don't propagate Redis errors, continue polling
                    }

                    // Poll at configured interval
                    await Task.Delay(pollIntervalMs, token);
                }

                observer.OnCompleted();
                _logger.LogInformation("Redis price watcher stopped for key: {Key}", indexKey);
            }
            catch (OperationCanceledException)
            {
                observer.OnCompleted();
                _logger.LogInformation("Redis price watcher cancelled for key: {Key}", indexKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in Redis price watcher for key: {Key}", indexKey);
                observer.OnError(ex);
            }
        });
    }
}

