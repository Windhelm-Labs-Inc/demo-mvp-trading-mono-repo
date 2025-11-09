using DotNetEnv;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace MarketMakerWorkerService.Tests;

public class RedisConnectionTests
{
    private readonly ITestOutputHelper _output;

    public RedisConnectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ConnectToRedis_AndListAllKeysAndValues()
    {
        // Load .env file first (for backward compatibility with REDIS_PASSWORD)
        Env.Load();
        
        // Build configuration with environment variables overriding appsettings
        var configuration = BuildConfiguration();
        
        // Get Redis connection string from configuration
        // Environment variable Redis__ConnectionString will override appsettings
        var connectionString = configuration["Redis:ConnectionString"];
        
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

        _output.WriteLine($"Connecting to Redis...");
        _output.WriteLine($"Connection string: {MaskPassword(connectionString)}");

        // Connect to Redis
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var db = redis.GetDatabase();

        _output.WriteLine("Connected successfully!");
        _output.WriteLine("");

        // Get all keys from the Redis server
        var endpoints = redis.GetEndPoints();
        var server = redis.GetServer(endpoints[0]);

        // Get all keys
        var keys = server.Keys(pattern: "*").ToList();

        _output.WriteLine($"Total Keys Found: {keys.Count}");
        _output.WriteLine("");

        if (keys.Count == 0)
        {
            _output.WriteLine("No keys found in Redis.");
            return;
        }

        // Print all keys and their values
        foreach (var key in keys)
        {
            var value = await db.StringGetAsync(key);
            var keyType = await db.KeyTypeAsync(key);
            
            _output.WriteLine($"Key: {key}");
            _output.WriteLine($"  Type: {keyType}");
            
            // Handle different data types
            switch (keyType)
            {
                case RedisType.String:
                    _output.WriteLine($"  Value: {value}");
                    break;
                    
                case RedisType.List:
                    var listValues = await db.ListRangeAsync(key);
                    _output.WriteLine($"  List Items ({listValues.Length}):");
                    foreach (var item in listValues)
                    {
                        _output.WriteLine($"    - {item}");
                    }
                    break;
                    
                case RedisType.Set:
                    var setValues = await db.SetMembersAsync(key);
                    _output.WriteLine($"  Set Members ({setValues.Length}):");
                    foreach (var item in setValues)
                    {
                        _output.WriteLine($"    - {item}");
                    }
                    break;
                    
                case RedisType.Hash:
                    var hashValues = await db.HashGetAllAsync(key);
                    _output.WriteLine($"  Hash Fields ({hashValues.Length}):");
                    foreach (var entry in hashValues)
                    {
                        _output.WriteLine($"    {entry.Name}: {entry.Value}");
                    }
                    break;
                    
                case RedisType.SortedSet:
                    var sortedSetValues = await db.SortedSetRangeByRankWithScoresAsync(key);
                    _output.WriteLine($"  Sorted Set Members ({sortedSetValues.Length}):");
                    foreach (var item in sortedSetValues)
                    {
                        _output.WriteLine($"    {item.Element} (score: {item.Score})");
                    }
                    break;
                    
                default:
                    _output.WriteLine($"  (Unsupported type: {keyType})");
                    break;
            }
            
            // Get TTL information
            var ttl = await db.KeyTimeToLiveAsync(key);
            if (ttl.HasValue)
            {
                _output.WriteLine($"  TTL: {ttl.Value.TotalSeconds:F0} seconds");
            }
            
            _output.WriteLine("");
        }
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
}

