using System.Text.Json;
using DotNetEnv;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;
using FluentAssertions;
using Xunit.Abstractions;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Tests.Helpers;

namespace MarketMakerWorkerService.Tests.Integration;

public class RedisConnectionIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly IRedisConnectionService _redisService;
    
    public RedisConnectionIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        
        // Load environment variables
        Env.Load();
        
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables()
            .Build();
        
        var config = TestConfiguration.CreateTestConfig();
        
        // Override with actual Redis connection
        var connectionString = GetRedisConnectionString(configuration);
        config.Value.RedisConnectionString = connectionString;
        
        _redisService = new RedisConnectionService(config, new Mock<ILogger<RedisConnectionService>>().Object);
        
        _output.WriteLine($"Connected to Redis: {_redisService.Connection.GetEndPoints()[0]}");
    }
    
    [Fact]
    public async Task TestConnectionAsync_ValidConnection_ReturnsTrue()
    {
        // Act
        var result = await _redisService.TestConnectionAsync();
        
        // Assert
        result.Should().BeTrue();
        
        _output.WriteLine("✓ Redis connection test passed");
    }
    
    [Fact]
    public async Task Connection_CanReadProductionIndexKey()
    {
        // Arrange
        // ⚠️ CRITICAL: We NEVER write to Redis! 
        // We only READ from the production Redis index publishing server.
        // The index prices are published by the production index service.
        var db = _redisService.Connection.GetDatabase();
        var indexKey = "spotindex:BTC_USD"; // Production index key
        
        // Act - READ ONLY from production Redis
        var value = await db.StringGetAsync(indexKey);
        
        // Assert
        value.HasValue.Should().BeTrue("Production index should always have a value");
        
        // Parse JSON to extract IndexPrice
        var jsonString = value.ToString();
        using var doc = JsonDocument.Parse(jsonString);
        var root = doc.RootElement;
        root.TryGetProperty("IndexPrice", out var indexPriceElement).Should().BeTrue("JSON should contain IndexPrice field");
        
        var price = indexPriceElement.GetDecimal();
        price.Should().BeGreaterThan(0, "BTC/USD price should be positive");
        
        _output.WriteLine($"✓ Successfully read production index key: {indexKey}");
        _output.WriteLine($"  Current BTC/USD index price: ${price:N2}");
        _output.WriteLine($"  Full JSON: {jsonString}");
    }
    
    [Fact]
    public async Task Connection_CanWatchProductionIndexChanges()
    {
        // Arrange
        // ⚠️ CRITICAL: We NEVER write to Redis!
        // This test watches REAL price updates from the production index service.
        var db = _redisService.Connection.GetDatabase();
        var indexKey = "spotindex:BTC_USD";
        var cts = new CancellationTokenSource(TestConfiguration.Timeouts.RedisWatchDuration);
        
        string? lastValue = null;
        var updateCount = 0;
        
        _output.WriteLine($"Watching production index for {TestConfiguration.Timeouts.RedisWatchDuration.TotalSeconds}s...");
        
        // Act - Poll and READ production index (like production code)
        while (!cts.Token.IsCancellationRequested)
        {
            var value = await db.StringGetAsync(indexKey);
            
            if (value.HasValue)
            {
                var currentValue = value.ToString();
                
                // Only count when value changes (like RedisReactiveWatchTests.cs)
                if (currentValue != lastValue)
                {
                    updateCount++;
                    lastValue = currentValue;
                    
                    // Parse JSON to extract IndexPrice
                    try
                    {
                        using var doc = JsonDocument.Parse(currentValue);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("IndexPrice", out var indexPriceElement))
                        {
                            var price = indexPriceElement.GetDecimal();
                            _output.WriteLine($"  Update #{updateCount}: ${price:N2}");
                        }
                    }
                    catch (JsonException)
                    {
                        _output.WriteLine($"  Update #{updateCount}: Raw value: {currentValue}");
                    }
                }
            }
            
            await Task.Delay(50, cts.Token); // 50ms polling like production
        }
        
        // Assert - We should get at least one value from production
        updateCount.Should().BeGreaterThanOrEqualTo(1, 
            "Production Redis should have at least one index price published");
        
        _output.WriteLine($"✓ Watched {updateCount} price updates from production index");
    }
    
    private string GetRedisConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration["Redis:ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException("Redis:ConnectionString not configured");
        
        // Add password if available
        if (!connectionString.Contains("password=", StringComparison.OrdinalIgnoreCase))
        {
            var password = Environment.GetEnvironmentVariable("REDIS_PASSWORD");
            if (!string.IsNullOrEmpty(password))
                connectionString = $"{connectionString},password={password}";
        }
        
        return connectionString;
    }
    
    public void Dispose()
    {
        (_redisService as IDisposable)?.Dispose();
    }
}

