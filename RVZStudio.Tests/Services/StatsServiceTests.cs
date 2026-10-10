using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RVZStudio.services;
using Xunit;

namespace RVZStudio.Tests.Services;

public class StatsServiceTests
{
    private const string ApiUrl = "https://stats.example.com/ApplicationStats/stats";
    private const string ApiKey = "test-api-key";
    private const string ApplicationId = "TestApp";

    [Fact]
    public async Task SendUsageStatsAsyncSendsPostWithBearerTokenAndJsonPayload()
    {
        using var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"message":"Stats recorded successfully","applicationId":"testapp"}""",
                MediaTypeHeaderValue.Parse("application/json"))
        });
        using var service = new StatsService(ApiUrl, ApiKey, ApplicationId, handler);

        await service.SendUsageStatsAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(ApiUrl, handler.LastRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal(ApiKey, handler.LastRequest.Headers.Authorization?.Parameter);

        Assert.NotNull(handler.LastRequestBody);
        using var json = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(ApplicationId, json.RootElement.GetProperty("applicationId").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("version").GetString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SendUsageStatsAsyncNonSuccessStatusDoesNotThrow(HttpStatusCode statusCode)
    {
        using var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("error")
        });
        using var service = new StatsService(ApiUrl, ApiKey, ApplicationId, handler);

        var exception = await Record.ExceptionAsync(service.SendUsageStatsAsync);

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendUsageStatsAsyncRateLimit429DoesNotThrow()
    {
        using var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("""{"error":"Rate limit exceeded"}""")
        });
        using var service = new StatsService(ApiUrl, ApiKey, ApplicationId, handler);

        var exception = await Record.ExceptionAsync(service.SendUsageStatsAsync);

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendUsageStatsAsyncNetworkErrorDoesNotThrow()
    {
        using var handler = new CapturingHttpMessageHandler(_ => throw new HttpRequestException("No network"));
        using var service = new StatsService(ApiUrl, ApiKey, ApplicationId, handler);

        var exception = await Record.ExceptionAsync(service.SendUsageStatsAsync);

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendUsageStatsAsyncUnexpectedErrorDoesNotThrow()
    {
        using var handler = new CapturingHttpMessageHandler(_ => throw new InvalidOperationException("boom"));
        using var service = new StatsService(ApiUrl, ApiKey, ApplicationId, handler);

        var exception = await Record.ExceptionAsync(service.SendUsageStatsAsync);

        Assert.Null(exception);
    }

    [Fact]
    public void DisposeCanBeCalledWithoutException()
    {
        using var handler = new CapturingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var service = new StatsService(ApiUrl, ApiKey, ApplicationId, handler);

        var exception = Record.Exception(service.Dispose);

        Assert.Null(exception);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult(_handler(request));
        }
    }
}
