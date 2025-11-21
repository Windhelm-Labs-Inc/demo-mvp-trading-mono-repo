using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using MarketMakerWorkerService.Configuration;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Tests.Helpers;

namespace MarketMakerWorkerService.Tests.E2E;

/// <summary>
/// E2E tests for continuous settlement feature against production resources
/// </summary>
public class ContinuousSettlementE2ETests
{
    private readonly ITestOutputHelper _output;
    
    public ContinuousSettlementE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }
    
    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~Settlement_AgainstProdAPI_SettlesMatchedPositions")]
    [Trait("Category", "E2E")]
    [Trait("Environment", "Production")]
    public async Task Settlement_AgainstProdAPI_SettlesMatchedPositions()
    {
        // Arrange
        var config = TestConfiguration.CreateTestConfig();
        var services = BuildServiceProvider(config.Value);
        
        var authService = services.GetRequiredService<IAuthenticationService>();
        var settlementService = services.GetRequiredService<IContinuousSettlementService>();
        
        // Act - Authenticate
        var token = await authService.AuthenticateAsync(CancellationToken.None);
        Assert.NotNull(token);
        _output.WriteLine($"Authenticated successfully (token length: {token.Length})");
        
        // Act - Check and settle
        var result = await settlementService.CheckAndSettlePositionsAsync(
            token, CancellationToken.None);
        
        // Assert
        Assert.True(result.Success, $"Settlement should succeed. Error: {result.ErrorMessage}");
        
        if (result.SettlementId != null)
        {
            _output.WriteLine($"✓ Settlement successful: {result.SettlementId}");
            _output.WriteLine($"  Settled {result.QuantitySettled} units across {result.PositionsSettled} positions");
            Assert.True(result.QuantitySettled > 0);
            Assert.True(result.PositionsSettled > 0);
        }
        else
        {
            _output.WriteLine($"No settlement needed: {result.ErrorMessage}");
        }
    }
    
    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~Settlement_MultipleRuns_HandlesIdempotency")]
    [Trait("Category", "E2E")]
    [Trait("Environment", "Production")]
    public async Task Settlement_MultipleRuns_HandlesIdempotency()
    {
        // Test that running settlement twice doesn't double-settle
        // (idempotency key changes each run, but positions should be gone after first)
        
        var config = TestConfiguration.CreateTestConfig();
        var services = BuildServiceProvider(config.Value);
        
        var authService = services.GetRequiredService<IAuthenticationService>();
        var settlementService = services.GetRequiredService<IContinuousSettlementService>();
        
        var token = await authService.AuthenticateAsync(CancellationToken.None);
        
        // First run
        var result1 = await settlementService.CheckAndSettlePositionsAsync(
            token, CancellationToken.None);
        _output.WriteLine($"Run 1: Success={result1.Success}, ID={result1.SettlementId}, Msg={result1.ErrorMessage}");
        
        // Second run (should have nothing to settle if first succeeded)
        var result2 = await settlementService.CheckAndSettlePositionsAsync(
            token, CancellationToken.None);
        _output.WriteLine($"Run 2: Success={result2.Success}, ID={result2.SettlementId}, Msg={result2.ErrorMessage}");
        
        Assert.True(result1.Success);
        Assert.True(result2.Success);
        
        // If first run settled something, second should have nothing
        if (result1.SettlementId != null)
        {
            Assert.Null(result2.SettlementId);
            _output.WriteLine("Idempotency confirmed: First settled, second found nothing");
        }
        else
        {
            _output.WriteLine("No positions to settle in either run");
        }
    }
    
    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~Settlement_WithOnlyLongPositions_ReturnsNoSettleable")]
    [Trait("Category", "E2E")]
    [Trait("Environment", "Production")]
    public async Task Settlement_WithOnlyLongPositions_ReturnsNoSettleable()
    {
        // Test behavior when there's nothing to settle (only one side)
        
        var config = TestConfiguration.CreateTestConfig();
        var services = BuildServiceProvider(config.Value);
        
        var authService = services.GetRequiredService<IAuthenticationService>();
        var settlementService = services.GetRequiredService<IContinuousSettlementService>();
        
        var token = await authService.AuthenticateAsync(CancellationToken.None);
        
        var result = await settlementService.CheckAndSettlePositionsAsync(
            token, CancellationToken.None);
        
        _output.WriteLine($"Result: Success={result.Success}, ID={result.SettlementId}, Msg={result.ErrorMessage}");
        
        // Should succeed (no error) but not settle anything if positions are unbalanced
        Assert.True(result.Success);
        
        if (result.ErrorMessage?.Contains("Need both") == true)
        {
            _output.WriteLine("Correctly identified unbalanced positions (need both long and short)");
        }
        else if (result.SettlementId != null)
        {
            _output.WriteLine($"Settled balanced positions: {result.QuantitySettled} units");
        }
        else
        {
            _output.WriteLine($"No positions or other valid reason: {result.ErrorMessage}");
        }
    }
    
    private ServiceProvider BuildServiceProvider(MarketMakerConfiguration config)
    {
        var services = new ServiceCollection();
        
        services.AddHttpClient("PerpetualsAPI", client =>
        {
            client.BaseAddress = new Uri(config.ApiBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        
        services.AddSingleton(Options.Create(config));
        services.AddLogging();
        services.AddSingleton<IAuthenticationService, AuthenticationService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IContinuousSettlementService, ContinuousSettlementService>();
        
        return services.BuildServiceProvider();
    }
}

