using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Protocol;

namespace KnowledgeBase.Mcp;

internal sealed class KnowledgeTools(GatewayClient gateway, ToolConcurrency concurrency)
{
    [Description("Search records and sources the configured gateway permits this client to access.")]
    public async Task<CallToolResult> Search(
        [Description("Search text (1 to 512 characters)."), StringLength(512, MinimumLength = 1)] string query,
        [Description("Maximum results (1 to 50)."), Range(1, 50)] int limit = 10,
        [Description("Opaque gateway cursor from a previous page, or null to start."), StringLength(4096)] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return McpResults.Error("invalid_request", false, false, null, null);
        return await InvokeAsync(() => gateway.SendAsync(HttpMethod.Post, "/v1/search", new { query, limit, cursor }, null, cancellationToken), cancellationToken);
    }

    [Description("Read one record allowed by the configured gateway client.")]
    public Task<CallToolResult> GetRecord([Description("Record UUID.")] Guid id, CancellationToken cancellationToken = default) =>
        id != Guid.Empty
            ? InvokeAsync(() => gateway.SendAsync(HttpMethod.Get, $"/v1/records/{id:D}", null, null, cancellationToken), cancellationToken)
            : Task.FromResult(McpResults.Error("invalid_request", false, false, null, null));

    [Description("Read an exact line range from a source allowed by the configured gateway client.")]
    public Task<CallToolResult> GetSourceText(
        [Description("Source UUID.")] Guid source_id,
        [Description("First line, starting at 1."), Range(1, int.MaxValue)] int start_line,
        [Description("Last line, inclusive."), Range(1, int.MaxValue)] int end_line,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(() => source_id != Guid.Empty && start_line > 0 && end_line >= start_line
            ? gateway.SendAsync(HttpMethod.Get, $"/v1/sources/{source_id:D}/text?start_line={start_line}&end_line={end_line}", null, null, cancellationToken)
            : Task.FromException<GatewayResponse>(new GatewayFailure("invalid_request", false, false, null, null)), cancellationToken);

    private async Task<CallToolResult> InvokeAsync(Func<Task<GatewayResponse>> action, CancellationToken cancellationToken)
    {
        try
        {
            using var lease = await concurrency.EnterAsync(cancellationToken);
            var response = await action();
            return McpResults.Success(GatewayClient.WrapData(response.Data, response.RequestId));
        }
        catch (GatewayFailure failure) { return McpResults.Error(failure.Code, failure.Retryable, failure.OutcomeUnknown, failure.RequestId, failure.IdempotencyKey); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return McpResults.Error("cancelled", false, false, null, null); }
        catch (OperationCanceledException) { return McpResults.Error("gateway_timeout", true, false, null, null); }
        catch (HttpRequestException) { return McpResults.Error("gateway_unavailable", true, false, null, null); }
        catch (Exception) { return McpResults.Error("gateway_protocol_error", false, false, null, null); }
    }
}

internal sealed class DraftTools(GatewayClient gateway, ToolConcurrency concurrency)
{
    [Description("Create a knowledge draft. Reuse the same idempotency_key to recover an unknown outcome.")]
    public async Task<CallToolResult> DraftRecord(
        [Description("Draft title (1 to 300 characters)."), StringLength(300, MinimumLength = 1)] string title,
        [Description("Draft body (1 to 65536 UTF-8 bytes)."), StringLength(65536, MinimumLength = 1)] string body,
        [Description("One to ten exact source citations."), MinLength(1), MaxLength(10)] CitationInput[] citations,
        [Description("Stable key to reuse after an unknown outcome (1 to 128 ASCII letters, digits, or - _ . :)."), StringLength(128, MinimumLength = 1)] string idempotency_key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body) || citations is null || citations.Length is < 1 or > 10 || citations.Any(c => c is null || c.SourceId == Guid.Empty || c.StartLine < 1 || c.EndLine < c.StartLine || string.IsNullOrWhiteSpace(c.Quote)) || idempotency_key is null || !ValidKey(idempotency_key) || System.Text.Encoding.UTF8.GetByteCount(body) > 65536)
            return McpResults.Error("invalid_request", false, false, null, null);
        var request = new { title, body, citations = citations.Select(c => new { source_id = c.SourceId, start_line = c.StartLine, end_line = c.EndLine, quote = c.Quote }).ToArray() };
        var requestStarted = false;
        try
        {
            using var lease = await concurrency.EnterAsync(cancellationToken);
            requestStarted = true;
            var response = await gateway.SendAsync(HttpMethod.Post, "/v1/drafts", request, idempotency_key, cancellationToken);
            return McpResults.Success(GatewayClient.WrapData(response.Data, response.RequestId), idempotency_key);
        }
        catch (GatewayFailure failure) { return McpResults.Error(failure.Code, failure.Retryable, failure.OutcomeUnknown, failure.RequestId, failure.IdempotencyKey); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return McpResults.Error(requestStarted ? "outcome_unknown" : "cancelled", false, requestStarted, null, requestStarted ? idempotency_key : null); }
        catch (OperationCanceledException) { return McpResults.Error("outcome_unknown", true, requestStarted, null, requestStarted ? idempotency_key : null); }
        catch (HttpRequestException) { return McpResults.Error("outcome_unknown", true, requestStarted, null, requestStarted ? idempotency_key : null); }
        catch (Exception) { return McpResults.Error("gateway_protocol_error", false, false, null, null); }
    }

    private static bool ValidKey(string key) => key.Length is > 0 and <= 128 && key.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or ':');
}

internal sealed class CitationInput
{
    [Required]
    [Description("Source UUID.")]
    public Guid SourceId { get; init; }

    [Range(1, int.MaxValue)]
    [Description("First cited line, starting at 1.")]
    public int StartLine { get; init; }

    [Range(1, int.MaxValue)]
    [Description("Last cited line, inclusive.")]
    public int EndLine { get; init; }

    [Required, StringLength(10000, MinimumLength = 1)]
    [Description("Exact text on the cited line range.")]
    public string Quote { get; init; } = "";
}
