using System.Reactive.Linq;
using System.Threading.Channels;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace MarketMakerWorkerService.Tests;

public class RedisReactiveWatchLegacyTests
{
    private readonly ITestOutputHelper _output;

    public RedisReactiveWatchLegacyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(2)]
    public async Task WatchBtcUsdKey_ReactivePattern_ForNSeconds(int seconds)
    {
        // Load .env file first (for backward compatibility with REDIS_PASSWORD)
        Env.Load();
        
        // Build configuration with environment variables overriding appsettings
        var configuration = BuildConfiguration();
        
        // Get Redis connection string from configuration
        // Environment variable Redis__ConnectionString will override appsettings
        var connectionString = configuration["Redis:ConnectionString"];
        var spotIndexPrefix = configuration["Redis:Keys:SpotIndexPrefix"];
        
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException(
                "Redis connection string is not set. Set Redis__ConnectionString environment variable or configure in appsettings.");
        }
        
        // If connection string doesn't contain password, try to append from environment
        if (!connectionString.Contains("password=", StringComparison.OrdinalIgnoreCase))
        {
            var redisPassword = Environment.GetEnvironmentVariable("REDIS_PASSWORD");
            if (!string.IsNullOrEmpty(redisPassword))
            {
                connectionString = $"{connectionString},password={redisPassword}";
            }
        }
        
        _output.WriteLine($"Starting Redis reactive watch on {spotIndexPrefix}BTC_USD for {seconds} second(s)...");
        _output.WriteLine($"Connection string: {MaskPassword(connectionString)}");
        _output.WriteLine("Watching for changes from production Index Publishing service.");
        _output.WriteLine("");

        // Connect to Redis
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var db = redis.GetDatabase();

        // Create a channel for streaming Redis updates
        var channel = Channel.CreateUnbounded<RedisUpdate>();
        
        // Create a cancellation token that expires after N seconds
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        
        var key = $"{spotIndexPrefix}BTC_USD";
        
        // Background task: Poll Redis and write updates to the channel
        var pollingTask = Task.Run(async () =>
        {
            string? lastValue = null;
            
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var value = await db.StringGetAsync(key);
                    
                    if (value.HasValue)
                    {
                        var currentValue = value.ToString();
                        
                        // Only emit if the value has changed
                        if (currentValue != lastValue)
                        {
                            await channel.Writer.WriteAsync(new RedisUpdate
                            {
                                Key = key,
                                Value = currentValue,
                                Timestamp = DateTime.UtcNow
                            }, cts.Token);
                            
                            lastValue = currentValue;
                        }
                    }
                    
                    // Poll every 50ms for responsive updates
                    await Task.Delay(50, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation token expires
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cts.Token);

        // Convert Channel to Observable using Rx.NET
        var observable = CreateObservableFromChannel(channel.Reader, cts.Token);

        // Track update count
        var updateCount = 0;

        // Subscribe to the observable stream and process updates
        await observable
            .Do(update =>
            {
                updateCount++;
                _output.WriteLine($"[Update #{updateCount}] at {update.Timestamp:HH:mm:ss.fff}");
                _output.WriteLine($"  Key: {update.Key}");
                _output.WriteLine($"  Value: {update.Value}");
                _output.WriteLine("");
            })
            .LastOrDefaultAsync(); // Wait for the stream to complete

        // Wait for polling task to complete
        await pollingTask;

        _output.WriteLine($"Reactive watch completed after {seconds} second(s). Total updates received: {updateCount}");
        
        // If production MarketMaker is running and updating every ~100ms, 
        // we should see multiple updates as the price changes
        // Note: We only emit on change, so the count depends on how often the value actually changes
        _output.WriteLine($"Note: Update count depends on how frequently the production value changes.");
        
        // Assert that we got at least some updates (verifies the reactive pipeline works)
        Assert.True(updateCount >= 1, 
            $"Should have received at least one update from Redis (got {updateCount}).");
    }

    /// <summary>
    /// Builds configuration from appsettings.json with environment variable overrides
    /// Environment variables override appsettings values when set.
    /// Use Redis__ConnectionString environment variable to override the entire connection string.
    /// </summary>
    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development"}.json", optional: true)
            .AddEnvironmentVariables() // Environment variables override appsettings (highest priority)
            .Build();
    }

    /// <summary>
    /// Masks password in connection string for logging
    /// </summary>
    private static string MaskPassword(string connectionString)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            connectionString, 
            @"password=([^,]+)", 
            "password=***", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Converts a ChannelReader to an IObservable for reactive processing
    /// This is the core of our reactive pipeline pattern
    /// </summary>
    private IObservable<RedisUpdate> CreateObservableFromChannel(
        ChannelReader<RedisUpdate> reader, 
        CancellationToken cancellationToken)
    {
        return Observable.Create<RedisUpdate>(async (observer, ct) =>
        {
            try
            {
                // Read from channel until it's completed
                await foreach (var item in reader.ReadAllAsync(ct))
                {
                    observer.OnNext(item);
                }
                
                observer.OnCompleted();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                observer.OnCompleted();
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        });
    }

    /// <summary>
    /// Represents a Redis key update in our reactive pipeline
    /// </summary>
    private record RedisUpdate
    {
        public required string Key { get; init; }
        public required string Value { get; init; }
        public required DateTime Timestamp { get; init; }
    }
}

