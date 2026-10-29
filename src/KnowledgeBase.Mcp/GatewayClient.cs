using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KnowledgeBase.Mcp;

internal sealed record McpSettings(Uri GatewayUrl, string Token, bool EnableDrafts, int TimeoutSeconds)
{
    public static McpSettings Load()
    {
        var rawUrl = Environment.GetEnvironmentVariable("KB_MCP_GATEWAY_URL");
        var token = Environment.GetEnvironmentVariable("KB_MCP_GATEWAY_TOKEN");
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(url.Host, out var address) || !IPAddress.IsLoopback(address) ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath is not ("/" or ""))
            throw new InvalidOperationException("KB_MCP_GATEWAY_URL must be an HTTP URL with a numeric loopback address and no path, query, fragment, or userinfo.");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("KB_MCP_GATEWAY_TOKEN is required.");
        var timeoutText = Environment.GetEnvironmentVariable("KB_MCP_HTTP_TIMEOUT_SECONDS");
        var timeout = string.IsNullOrEmpty(timeoutText) ? 15 : int.TryParse(timeoutText, out var parsed) ? parsed : 0;
        if (timeout is < 1 or > 300) throw new InvalidOperationException("KB_MCP_HTTP_TIMEOUT_SECONDS must be between 1 and 300.");
        var draftsText = Environment.GetEnvironmentVariable("KB_MCP_ENABLE_DRAFTS");
        if (!string.IsNullOrEmpty(draftsText) && !bool.TryParse(draftsText, out _)) throw new InvalidOperationException("KB_MCP_ENABLE_DRAFTS must be true or false.");
        return new(url, token, string.Equals(draftsText, "true", StringComparison.OrdinalIgnoreCase), timeout);
    }
}

internal sealed class GatewayClient : IDisposable
{
    private const int MaxHttpResponseBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient client;

    public GatewayClient(McpSettings settings)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        client = new HttpClient(handler) { BaseAddress = settings.GatewayUrl, Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
    }

    public void Dispose() => client.Dispose();

    public async Task<GatewayResponse> SendAsync(HttpMethod method, string route, object? body, string? idempotencyKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var requestId = response.Headers.TryGetValues("X-Request-Id", out var values) ? values.FirstOrDefault() : null;
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken, idempotencyKey, requestId);
        if (!response.IsSuccessStatusCode)
        {
            var code = ReadErrorCode(bytes);
            throw new GatewayFailure(MapError((int)response.StatusCode, code), IsRetryable((int)response.StatusCode, code), idempotencyKey is not null && (int)response.StatusCode == 503, requestId, idempotencyKey);
        }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return new(document.RootElement.Clone(), requestId);
        }
        catch (JsonException) { throw new GatewayFailure("gateway_protocol_error", false, idempotencyKey is not null, requestId, idempotencyKey); }
    }

    public static JsonElement WrapData(JsonElement data, string? requestId)
    {
        var wrapper = new Dictionary<string, object?> { ["ok"] = true, ["data"] = data };
        if (requestId is not null) wrapper["gateway_request_id"] = requestId;
        return JsonSerializer.SerializeToElement(wrapper, Json);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken, string? idempotencyKey, string? requestId)
    {
        if (content.Headers.ContentLength > MaxHttpResponseBytes) throw new GatewayFailure("response_limit_exceeded", false, idempotencyKey is not null, requestId, idempotencyKey);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaxHttpResponseBytes) throw new GatewayFailure("response_limit_exceeded", false, idempotencyKey is not null, requestId, idempotencyKey);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string? ReadErrorCode(byte[] body)
    {
        try { return JsonDocument.Parse(body).RootElement.GetProperty("error").GetString(); }
        catch { return null; }
    }

    private static string MapError(int status, string? code) => code switch
    {
        "retry_required" or "idempotency_conflict" or "operation_recovery_required" => code,
        "invalid_request" or "request_too_large" or "unauthorized" or "forbidden" or "resource_unavailable" or "rate_limited" or "service_unavailable" => code,
        _ => status switch { 400 => "invalid_request", 413 => "request_too_large", 401 => "unauthorized", 403 => "forbidden", 404 => "resource_unavailable", 409 => "retry_required", 429 => "rate_limited", 503 => "service_unavailable", _ => "gateway_protocol_error" }
    };

    private static bool IsRetryable(int status, string? code) => status is 429 or 503 || code == "retry_required";
}

internal sealed record GatewayResponse(JsonElement Data, string? RequestId);

internal sealed class GatewayFailure(string code, bool retryable, bool outcomeUnknown, string? requestId, string? idempotencyKey) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
    public string? RequestId { get; } = requestId;
    public string? IdempotencyKey { get; } = idempotencyKey;
}

internal sealed class ToolConcurrency
{
    private readonly SemaphoreSlim semaphore = new(4, 4);
    public Task<IDisposable> EnterAsync(CancellationToken cancellationToken) => EnterCoreAsync(cancellationToken);
    private async Task<IDisposable> EnterCoreAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        return new Lease(semaphore);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable { public void Dispose() => semaphore.Release(); }
}

internal static class McpResults
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static ModelContextProtocol.Protocol.CallToolResult Success(JsonElement payload, string? idempotencyKey = null)
    {
        var serialized = JsonSerializer.Serialize(payload, Json);
        if (Encoding.UTF8.GetByteCount(serialized) * 2 + 256 > 256 * 1024)
        {
            var requestId = payload.TryGetProperty("gateway_request_id", out var id) ? id.GetString() : null;
            return Error("response_limit_exceeded", false, idempotencyKey is not null, requestId, idempotencyKey);
        }
        return new() { StructuredContent = payload, Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = serialized }] };
    }
    public static ModelContextProtocol.Protocol.CallToolResult Error(string code, bool retryable, bool unknown, string? requestId, string? key)
    {
        var error = new Dictionary<string, object?> { ["code"] = code, ["retryable"] = retryable, ["outcome_unknown"] = unknown };
        if (requestId is not null) error["gateway_request_id"] = requestId;
        if (unknown && key is not null) error["idempotency_key"] = key;
        var result = JsonSerializer.SerializeToElement(new { ok = false, error }, Json);
        return new() { IsError = true, StructuredContent = result, Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = JsonSerializer.Serialize(result, Json) }] };
    }
}
