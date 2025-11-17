using Moq;
using Moq.Protected;
using System.Net;
using System.Text;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MarketMakerWorkerService.Services;
using MarketMakerWorkerService.Models;
using MarketMakerWorkerService.Tests.Helpers;

namespace MarketMakerWorkerService.Tests.Unit;

public class MarketDataServiceTests
{
    [Fact]
    public async Task GetMarketInfoAsync_ValidResponse_ReturnsMarketInfo()
    {
        // Arrange
        var mockResponse = new MarketInfoResponse(
            ChainId: TestConfiguration.MarketInfo.ChainId,
            LedgerId: TestConfiguration.MarketInfo.LedgerId,
            TradingPair: TestConfiguration.MarketInfo.TradingPair,
            SettlementToken: EntityId.FromString(TestConfiguration.MarketInfo.SettlementToken),
            TradingDecimals: TestConfiguration.MarketInfo.TradingDecimals,
            SettlementDecimals: TestConfiguration.MarketInfo.SettlementDecimals
        );
        
        var mockHttpFactory = CreateMockHttpFactory(
            endpoint: "/api/v1/market/info",
            response: mockResponse
        );
        
        var logger = new Mock<ILogger<MarketDataService>>();
        var service = new MarketDataService(mockHttpFactory.Object, logger.Object);
        
        // Act
        var result = await service.GetMarketInfoAsync(CancellationToken.None);
        
        // Assert
        result.Should().NotBeNull();
        result.TradingDecimals.Should().Be(TestConfiguration.MarketInfo.TradingDecimals);
        result.SettlementDecimals.Should().Be(TestConfiguration.MarketInfo.SettlementDecimals);
        result.TradingPair.Should().Be(TestConfiguration.MarketInfo.TradingPair);
    }
    
    [Fact]
    public async Task GetSpreadAsync_ValidResponse_ReturnsSpread()
    {
        // Arrange
        var mockResponse = new SpreadResponse(
            BestBid: TestConfiguration.SamplePrices.BestBidBaseUnits,
            BestAsk: TestConfiguration.SamplePrices.BestAskBaseUnits,
            BidCount: TestConfiguration.SamplePrices.BidCount,
            AskCount: TestConfiguration.SamplePrices.AskCount
        );
        
        var mockHttpFactory = CreateMockHttpFactory(
            endpoint: "/api/v1/market/spread",
            response: mockResponse
        );
        
        var logger = new Mock<ILogger<MarketDataService>>();
        var service = new MarketDataService(mockHttpFactory.Object, logger.Object);
        
        // Act
        var result = await service.GetSpreadAsync(CancellationToken.None);
        
        // Assert
        result.Should().NotBeNull();
        result.BestBid.Should().Be(TestConfiguration.SamplePrices.BestBidBaseUnits);
        result.BestAsk.Should().Be(TestConfiguration.SamplePrices.BestAskBaseUnits);
    }
    
    [Fact]
    public async Task GetMarketInfoAsync_ApiError_ThrowsException()
    {
        // Arrange
        var mockHttpFactory = CreateMockHttpFactory(
            endpoint: "/api/v1/market/info",
            response: new { error = "Not found" },
            statusCode: HttpStatusCode.NotFound
        );
        
        var logger = new Mock<ILogger<MarketDataService>>();
        var service = new MarketDataService(mockHttpFactory.Object, logger.Object);
        
        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetMarketInfoAsync(CancellationToken.None));
    }
    
    private Mock<IHttpClientFactory> CreateMockHttpFactory<T>(
        string endpoint,
        T response,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var mockHandler = new Mock<HttpMessageHandler>();
        
        mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains(endpoint)),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(response),
                    Encoding.UTF8,
                    "application/json")
            });
        
        var client = new HttpClient(mockHandler.Object)
        {
            BaseAddress = new Uri("https://test-api.example.com")
        };
        
        mockFactory.Setup(f => f.CreateClient("PerpetualsAPI")).Returns(client);
        
        return mockFactory;
    }
}

