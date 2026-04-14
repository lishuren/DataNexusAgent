using System.Net;
using DataNexus.Core;
using DataNexus.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataNexus.Tests;

public sealed class OutputIntegratorPluginTests
{
    [Fact]
    public async Task ExecuteAsync_UsesEndpointAliasFromMetadata()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}")
        });
        var plugin = CreatePlugin(handler);

        var result = await plugin.ExecuteAsync(new PluginContext(
            "user-123",
            "{\"invoiceId\":42}",
            Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["endpoint"] = "https://api.example.com/v1/invoices"
            }));

        Assert.True(result.Success);
        Assert.Equal("{\"ok\":true}", result.Output);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://api.example.com/v1/invoices", handler.LastUri?.ToString());
        Assert.Equal("{\"invoiceId\":42}", handler.LastBody);
    }

    [Fact]
    public async Task ExecuteAsync_PassthroughsStructuredOutputFormatsWithoutEndpoint()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var plugin = CreatePlugin(handler);

        var result = await plugin.ExecuteAsync(new PluginContext(
            "user-123",
            "{\"invoiceId\":42}",
            Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["OutputDestination"] = "JSON"
            }));

        Assert.True(result.Success);
        Assert.Equal("{\"invoiceId\":42}", result.Output);
        Assert.Null(handler.LastMethod);
        Assert.Null(handler.LastUri);
    }

    [Fact]
    public async Task ExecuteAsync_FailsWhenApiDestinationHasNoEndpoint()
    {
        var plugin = CreatePlugin(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var result = await plugin.ExecuteAsync(new PluginContext(
            "user-123",
            "{\"invoiceId\":42}",
            Metadata: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Destination"] = "api"
            }));

        Assert.False(result.Success);
        Assert.Equal("EXECUTION_ERROR", result.ErrorCode);
        Assert.Equal("ApiEndpoint not specified in metadata", result.ErrorMessage);
    }

    private static OutputIntegratorPlugin CreatePlugin(HttpMessageHandler handler) =>
        new(new TestHttpClientFactory(handler), NullLogger<OutputIntegratorPlugin>.Instance);

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return responseFactory(request);
        }
    }
}