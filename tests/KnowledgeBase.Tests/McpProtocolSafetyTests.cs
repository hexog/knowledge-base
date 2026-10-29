using System.Net;
using System.Text;
using System.Text.Json;
using KnowledgeBase.Mcp;
using KnowledgeBase.Configuration;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeBase.Tests;

public sealed class McpProtocolSafetyTests
{
    [Fact]
    public async Task GatewayDeadlineIncludesResponseBodyAndMarksDraftOutcomeUnknown()
    {
        using var handler = new ImmediateHeadersHandler(new HangingContent());
        using var gateway = new GatewayClient(Settings(timeoutSeconds: 1), handler);

        var failure = await Assert.ThrowsAsync<GatewayFailure>(() => gateway.SendAsync(
            HttpMethod.Post, "/v1/drafts", new { title = "draft" }, "stable-key", CancellationToken.None));

        Assert.Equal("gateway_timeout", failure.Code);
        Assert.True(failure.OutcomeUnknown);
        Assert.Equal("stable-key", failure.IdempotencyKey);
        Assert.Equal("gateway-request", failure.RequestId);
    }

    [Fact]
    public async Task GatewayDeadlineDoesNotReplaceMcpCallerCancellation()
    {
        using var handler = new ImmediateHeadersHandler(new HangingContent());
        using var gateway = new GatewayClient(Settings(timeoutSeconds: 30), handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.SendAsync(
            HttpMethod.Get, "/v1/records/00000000-0000-0000-0000-000000000001", null, null, cancellation.Token));
    }

    [Fact]
    public async Task EmptySuccessfulGatewayResponseIsProtocolError()
    {
        using var handler = new ImmediateHeadersHandler(new StringContent("{}", Encoding.UTF8, "application/json"));
        using var gateway = new GatewayClient(Settings(timeoutSeconds: 5), handler);

        var failure = await Assert.ThrowsAsync<GatewayFailure>(() => gateway.SendAsync(
            HttpMethod.Post, "/v1/search", new { query = "x" }, null, CancellationToken.None));

        Assert.Equal("gateway_protocol_error", failure.Code);
        Assert.False(failure.OutcomeUnknown);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\\')]
    [InlineData('x')]
    public void McpResultLimitUsesTheSerializedCallToolResult(char character)
    {
        var value = new string(character, 100_000);
        var payload = JsonSerializer.SerializeToElement(new
        {
            ok = true,
            data = new { text = value },
            gateway_request_id = "gateway-request"
        });
        var text = JsonSerializer.Serialize(payload);
        var fullResult = new CallToolResult
        {
            StructuredContent = payload,
            Content = [new TextContentBlock { Text = text }]
        };
        var serializedSize = JsonSerializer.SerializeToUtf8Bytes(fullResult, McpJsonUtilities.DefaultOptions).Length;

        var actual = McpResults.Success(payload);

        Assert.Equal(serializedSize <= 256 * 1024, actual.IsError != true);
        if (actual.IsError == true)
        {
            Assert.Contains("response_limit_exceeded", JsonSerializer.Serialize(actual.StructuredContent));
        }
        else
        {
            Assert.Equal(serializedSize, JsonSerializer.SerializeToUtf8Bytes(actual, McpJsonUtilities.DefaultOptions).Length);
        }
    }

    [Fact]
    public void GatewayResponseValidationRejectsMissingAndWronglyTypedFields()
    {
        using var empty = JsonDocument.Parse("{}");
        using var invalidSearch = JsonDocument.Parse("""{"results":{},"cursor":null,"policy_revision":1}""");

        Assert.Throws<JsonException>(() => GatewayClient.ValidateResponse(HttpMethod.Post, "/v1/search", empty.RootElement));
        Assert.Throws<JsonException>(() => GatewayClient.ValidateResponse(HttpMethod.Post, "/v1/search", invalidSearch.RootElement));
    }

    [Theory]
    [InlineData("POST", "/v1/search")]
    [InlineData("GET", "/v1/records/00000000-0000-0000-0000-000000000001")]
    [InlineData("GET", "/v1/sources/00000000-0000-0000-0000-000000000001/text?start_line=1&end_line=1")]
    [InlineData("POST", "/v1/drafts")]
    public void EmptySuccessBodyIsRejectedForEveryGatewayRoute(string method, string route)
    {
        using var empty = JsonDocument.Parse("{}");

        Assert.Throws<JsonException>(() => GatewayClient.ValidateResponse(new HttpMethod(method), route, empty.RootElement));
    }

    private static McpSettings Settings(int timeoutSeconds) =>
        new(new Uri("http://127.0.0.1:8765"), "test-token", false, timeoutSeconds);

    private sealed class ImmediateHeadersHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.Add("X-Request-Id", "gateway-request");
            return Task.FromResult(response);
        }
    }

    private sealed class HangingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 1; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new HangingStream());
    }

    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => WaitForCancellation(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static async ValueTask<int> WaitForCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
