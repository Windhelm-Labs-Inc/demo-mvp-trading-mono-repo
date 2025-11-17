using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Tests.Helpers;

namespace MarketMakerWorkerService.Tests.Unit;

public class AuthenticationServiceTests
{
    [Fact]
    public async Task AuthenticateAsync_ValidCredentials_ReturnsToken()
    {
        // Arrange
        var mockHttpFactory = CreateMockHttpClientFactory(
            challengeResponse: new { challenge_id = TestConfiguration.MockApi.ChallengeId, message = TestConfiguration.MockApi.ChallengeMessage },
            verifyResponse: new { access_token = TestConfiguration.MockApi.MockJwtToken, expires_in = TestConfiguration.MockApi.TokenExpirySeconds }
        );
        
        var config = TestConfiguration.CreateTestConfig();
        var logger = new Mock<ILogger<AuthenticationService>>();
        
        var authService = new AuthenticationService(mockHttpFactory.Object, config, logger.Object);
        
        // Act
        var token = await authService.AuthenticateAsync(CancellationToken.None);
        
        // Assert
        token.Should().NotBeNullOrEmpty();
        token.Should().Be(TestConfiguration.MockApi.MockJwtToken);
    }
    
    [Fact]
    public async Task GetValidTokenAsync_TokenExpired_RefreshesToken()
    {
        // Arrange
        var mockHttpFactory = CreateMockHttpClientFactory(
            challengeResponse: new { challenge_id = TestConfiguration.MockApi.ChallengeId, message = TestConfiguration.MockApi.ChallengeMessage },
            verifyResponse: new { access_token = "new-token", expires_in = TestConfiguration.MockApi.TokenExpirySeconds }
        );
        
        var config = TestConfiguration.CreateTestConfig();
        var logger = new Mock<ILogger<AuthenticationService>>();
        
        var authService = new AuthenticationService(mockHttpFactory.Object, config, logger.Object);
        
        // Act
        var token1 = await authService.GetValidTokenAsync(CancellationToken.None);
        await Task.Delay(100);
        var token2 = await authService.GetValidTokenAsync(CancellationToken.None);
        
        // Assert - Should return same token (not expired)
        token1.Should().Be(token2);
    }
    
    private Mock<IHttpClientFactory> CreateMockHttpClientFactory(
        object challengeResponse,
        object verifyResponse,
        HttpStatusCode challengeStatusCode = HttpStatusCode.OK,
        HttpStatusCode verifyStatusCode = HttpStatusCode.OK)
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var mockHandler = new Mock<HttpMessageHandler>();
        
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/auth/challenge")),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = challengeStatusCode,
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(challengeResponse), Encoding.UTF8, "application/json")
            });
        
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("/auth/verify")),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = verifyStatusCode,
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(verifyResponse), Encoding.UTF8, "application/json")
            });
        
        var client = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("https://test-api.example.com")
        };
        
        mockFactory.Setup(f => f.CreateClient("PerpetualsAPI")).Returns(client);
        
        return mockFactory;
    }
}

