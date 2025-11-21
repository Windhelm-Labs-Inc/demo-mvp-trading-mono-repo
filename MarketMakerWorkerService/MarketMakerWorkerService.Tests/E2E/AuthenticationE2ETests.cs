using WireMock.Server;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Moq;
using Xunit;
using FluentAssertions;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Tests.Helpers;

namespace MarketMakerWorkerService.Tests.E2E;

public class AuthenticationE2ETests : IDisposable
{
    private readonly WireMockServer _mockServer;
    private readonly ITestOutputHelper _output;
    
    public AuthenticationE2ETests(ITestOutputHelper output)
    {
        _output = output;
        _mockServer = WireMockServer.Start();
        SetupMockEndpoints();
        
        _output.WriteLine($"Mock API server started at: {_mockServer.Url}");
    }
    
    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~E2E_AuthenticationFlow_CompleteSuccess")]
    public async Task E2E_AuthenticationFlow_CompleteSuccess()
    {
        // Arrange
        var config = TestConfiguration.CreateTestConfig(apiBaseUrl: _mockServer.Url!);
        
        var services = new ServiceCollection();
        services.AddHttpClient("PerpetualsAPI", client =>
        {
            client.BaseAddress = new Uri(_mockServer.Url!);
        });
        services.AddLogging();
        
        var httpFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var logger = new Mock<ILogger<AuthenticationService>>();
        
        var authService = new AuthenticationService(httpFactory, config, logger.Object);
        
        _output.WriteLine("Starting full authentication E2E test");
        
        // Act
        var token = await authService.AuthenticateAsync(CancellationToken.None);
        
        // Assert
        token.Should().NotBeNullOrEmpty();
        token.Should().Be(TestConfiguration.MockApi.MockJwtToken);
        
        // Verify the mock server received both requests
        var challengeRequests = _mockServer.LogEntries
            .Count(e => e.RequestMessage.Path.Contains("/auth/challenge"));
        var verifyRequests = _mockServer.LogEntries
            .Count(e => e.RequestMessage.Path.Contains("/auth/verify"));
        
        challengeRequests.Should().Be(1);
        verifyRequests.Should().Be(1);
        
        _output.WriteLine("✓ Full authentication flow completed successfully");
        _output.WriteLine($"  - Challenge requests: {challengeRequests}");
        _output.WriteLine($"  - Verify requests: {verifyRequests}");
        _output.WriteLine($"  - Token received: {token.Substring(0, Math.Min(20, token.Length))}...");
    }
    
    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~E2E_GetValidTokenAsync_TokenCaching_ReusesSameToken")]
    public async Task E2E_GetValidTokenAsync_TokenCaching_ReusesSameToken()
    {
        // Arrange
        var config = TestConfiguration.CreateTestConfig(apiBaseUrl: _mockServer.Url!);
        
        var services = new ServiceCollection();
        services.AddHttpClient("PerpetualsAPI", client =>
        {
            client.BaseAddress = new Uri(_mockServer.Url!);
        });
        services.AddLogging();
        
        var httpFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var logger = new Mock<ILogger<AuthenticationService>>();
        
        var authService = new AuthenticationService(httpFactory, config, logger.Object);
        
        _output.WriteLine("Testing token caching behavior");
        
        // Act - Call twice
        var token1 = await authService.GetValidTokenAsync(CancellationToken.None);
        await Task.Delay(100);
        var token2 = await authService.GetValidTokenAsync(CancellationToken.None);
        
        // Assert
        token1.Should().Be(token2);
        
        // Verify only one authentication flow occurred
        var challengeRequests = _mockServer.LogEntries
            .Count(e => e.RequestMessage.Path.Contains("/auth/challenge"));
        
        challengeRequests.Should().Be(1);
        
        _output.WriteLine("✓ Token caching working correctly");
        _output.WriteLine($"  - Same token returned: {token1 == token2}");
        _output.WriteLine($"  - Challenge requests: {challengeRequests} (expected: 1)");
    }
    
    [Fact(Skip = "Explicit E2E test - run with: dotnet test --filter FullyQualifiedName~E2E_AuthenticationFailure_ThrowsException")]
    public async Task E2E_AuthenticationFailure_ThrowsException()
    {
        // Arrange - Set up a mock that returns 401 for verify
        var mockServer = WireMockServer.Start();
        
        mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/auth/challenge")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    challenge_id = TestConfiguration.MockApi.ChallengeId,
                    message = TestConfiguration.MockApi.ChallengeMessage
                }));
        
        mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/auth/verify")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(401)
                .WithBodyAsJson(new { error = "Invalid signature" }));
        
        var config = TestConfiguration.CreateTestConfig(apiBaseUrl: mockServer.Url!);
        
        var services = new ServiceCollection();
        services.AddHttpClient("PerpetualsAPI", client =>
        {
            client.BaseAddress = new Uri(mockServer.Url!);
        });
        services.AddLogging();
        
        var httpFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var logger = new Mock<ILogger<AuthenticationService>>();
        
        var authService = new AuthenticationService(httpFactory, config, logger.Object);
        
        _output.WriteLine("Testing authentication failure scenario");
        
        // Act & Assert
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => authService.AuthenticateAsync(CancellationToken.None));
        
        exception.Message.Should().Contain("401");
        
        _output.WriteLine("✓ Authentication failure handled correctly");
        
        mockServer.Stop();
        mockServer.Dispose();
    }
    
    private void SetupMockEndpoints()
    {
        // Mock challenge endpoint
        _mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/auth/challenge")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new
                {
                    challenge_id = TestConfiguration.MockApi.ChallengeId,
                    message = TestConfiguration.MockApi.ChallengeMessage,
                    expires_at_utc = "2025-12-31T23:59:59Z"
                }));
        
        // Mock verify endpoint
        _mockServer
            .Given(Request.Create()
                .WithPath("/api/v1/auth/verify")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(new
                {
                    access_token = TestConfiguration.MockApi.MockJwtToken,
                    expires_in = TestConfiguration.MockApi.TokenExpirySeconds
                }));
    }
    
    public void Dispose()
    {
        _mockServer?.Stop();
        _mockServer?.Dispose();
        
        _output.WriteLine("Mock API server stopped");
    }
}

