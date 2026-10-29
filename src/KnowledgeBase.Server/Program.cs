using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeBase.Configuration;
using KnowledgeBase.Core;

var configuration = ApplicationConfiguration.Load();
var settings = ServerSettings.Load(configuration);
var database = settings.DatabasePath;
var state = settings.StatePath;
var registryPath = settings.ClientRegistryPath;
Directory.CreateDirectory(state);
using var processLock = AcquireServerLock(Path.Combine(state, ".server.lock"));
var store = new KnowledgeBaseStore(database); store.Initialize();
if (store.SchemaVersion != "3") throw new InvalidOperationException("Run 'kb migrate --to 3' before starting the agent server");
store.RecoverServerState();
var cursorKeyPath = Path.Combine(state, "cursor.key");
if (!File.Exists(cursorKeyPath)) { File.WriteAllBytes(cursorKeyPath, RandomNumberGenerator.GetBytes(32)); ClientRegistry.Restrict(cursorKeyPath); }
if (new FileInfo(cursorKeyPath).Length != 32) throw new InvalidDataException("Server cursor key is invalid");
ClientRegistry.Restrict(cursorKeyPath);
var service = new AgentKnowledgeService(store, File.ReadAllBytes(cursorKeyPath));
var clients = new ClientRegistry(registryPath); var audit = new AuditWriter(state); var idempotency = new IdempotencyStore(state);
var requestGate = new SemaphoreSlim(8, 8); var queued = 0;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => { options.Listen(System.Net.IPAddress.Loopback, settings.Port); options.Limits.MaxRequestBodySize = 1024 * 1024; });
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Pragma = "no-cache";
    context.Items["request_id"] = Guid.NewGuid().ToString("N"); context.Response.Headers["X-Request-Id"] = (string)context.Items["request_id"]!;
    if (context.Request.Headers.ContainsKey("Origin")) { await WriteError(context, 403, "forbidden"); return; }
    if (context.Request.ContentLength > 1024 * 1024) { await WriteError(context, 413, "request_too_large"); return; }
    if (!context.Request.Path.StartsWithSegments("/v1")) { await next(); return; }
    var requiredScope = context.Request.Path.StartsWithSegments("/v1/search") ? "search" :
        context.Request.Path.StartsWithSegments("/v1/drafts") ? "draft" :
        context.Request.Path.StartsWithSegments("/v1/records") || context.Request.Path.StartsWithSegments("/v1/sources") ? "read" : null;
    var credential = Authenticate(context);
    if (credential is null)
    {
        var unavailable = context.Items.ContainsKey("auth_error");
        if (!unavailable) { try { audit.Write(RequestId(context), "anonymous", "unknown", "authenticate", "unauthorized", store.PolicyRevision); } catch { } }
        await WriteError(context, unavailable ? 503 : 401, unavailable ? "service_unavailable" : "unauthorized"); return;
    }
    if (requiredScope is not null && !HasScope(credential, requiredScope))
    {
        try { audit.Write(RequestId(context), credential.ClientId, credential.Profile, requiredScope, "forbidden", store.PolicyRevision); } catch { }
        await WriteError(context, 403, "forbidden"); return;
    }
    if (Interlocked.Increment(ref queued) > 24) { Interlocked.Decrement(ref queued); await WriteError(context, 429, "rate_limited"); return; }
    try
    {
        if (!await requestGate.WaitAsync(TimeSpan.FromSeconds(5), context.RequestAborted)) { await WriteError(context, 503, "retry_required"); return; }
        try
        {
            try { await next(); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted) { await WriteError(context, 413, "request_too_large"); }
        }
        finally { requestGate.Release(); }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    finally { Interlocked.Decrement(ref queued); }
});

app.MapPost("/v1/search", async (HttpContext context) =>
{
    var auth = Authenticate(context); if (auth is null) return await AuthenticationFailure(context);
    if (!HasScope(auth, "search")) return await Denied(context, auth, "search", "forbidden");
    var requestId = RequestId(context);
    try
    {
        var req = await ReadJson<SearchRequest>(context);
        if (req is null) { LogRequestFailure(context, "search", "request body is empty or invalid"); return await ErrorResult(context, 400, "invalid_request"); }
        if (string.IsNullOrWhiteSpace(req.Query) || req.Query.Length > 512) { LogRequestFailure(context, "search", "query must contain 1 to 512 characters"); return await ErrorResult(context, 400, "invalid_request"); }
        if (req.Limit is < 1 or > 50) { LogRequestFailure(context, "search", "limit must be between 1 and 50"); return await ErrorResult(context, 400, "invalid_request"); }
        using var clientGuard = clients.AcquireGuard(context.RequestAborted);
        using var guard = store.AcquirePolicyGuard(context.RequestAborted);
        if (!StillAuthorized(context, auth, "search")) return await Revoked(context, auth, "search");
        var revision = store.PolicyRevision; var page = service.Search(req.Query, req.Limit, req.Cursor, auth.Profile, auth.ClientId, auth.Scopes);
        if (revision != store.PolicyRevision) return await Fail(context, auth, "search", revision, 409, "retry_required");
        var results = page.Results.Select(x => new { x.Id, x.Version, x.Type, x.Kind, x.Status, x.Title, snippet = Snippet(x.Body, req.Query), citations = x.Citations.Select(CitationDto).ToArray() }).ToArray();
        audit.Write(requestId, auth.ClientId, auth.Profile, "search", "ok", revision, page.Results.Select(x => x.Id));
        return Results.Ok(new { results, cursor = page.Cursor, policy_revision = revision });
    }
    catch (Exception ex) { return await HandleFailure(context, ex, auth, "search"); }
});

app.MapGet("/v1/records/{id:guid}", async (HttpContext context, Guid id) =>
{
    var auth = Authenticate(context); if (auth is null) return await AuthenticationFailure(context);
    if (!HasScope(auth, "read")) return await Denied(context, auth, "record_read", "forbidden");
    try
    {
        using var clientGuard = clients.AcquireGuard(context.RequestAborted); using var guard = store.AcquirePolicyGuard(context.RequestAborted); if (!StillAuthorized(context, auth, "read")) return await Revoked(context, auth, "record_read");
        var revision = store.PolicyRevision; var record = service.GetRecord(id, auth.Profile);
        if (revision != store.PolicyRevision) return await Fail(context, auth, "record_read", revision, 409, "retry_required");
        var result = new { record.Id, record.Version, record.Type, record.Kind, record.Status, record.Title, record.Body, citations = record.Citations.Select(CitationDto).ToArray(), policy_revision = revision };
        audit.Write(RequestId(context), auth.ClientId, auth.Profile, "record_read", "ok", revision, new[] { id }); return Results.Ok(result);
    }
    catch (Exception ex) { return await HandleFailure(context, ex, auth, "record_read"); }
});

app.MapGet("/v1/sources/{id:guid}/text", async (HttpContext context, Guid id) =>
{
    var auth = Authenticate(context); if (auth is null) return await AuthenticationFailure(context);
    if (!HasScope(auth, "read")) return await Denied(context, auth, "source_read", "forbidden");
    try
    {
        if (!int.TryParse(context.Request.Query["start_line"], out var start_line) || !int.TryParse(context.Request.Query["end_line"], out var end_line)) { LogRequestFailure(context, "source_read", "start_line and end_line must be integers"); return await ErrorResult(context, 400, "invalid_request"); }
        using var clientGuard = clients.AcquireGuard(context.RequestAborted); using var guard = store.AcquirePolicyGuard(context.RequestAborted); if (!StillAuthorized(context, auth, "read")) return await Revoked(context, auth, "source_read");
        var revision = store.PolicyRevision; var source = service.GetSourceText(id, start_line, end_line, auth.Profile);
        if (revision != store.PolicyRevision) return await Fail(context, auth, "source_read", revision, 409, "retry_required");
        var result = new { source_id = source.SourceId, name = source.Name, origin_urls = source.OriginUrls, start_line, end_line = source.EndLine, text = source.Text, policy_revision = revision };
        audit.Write(RequestId(context), auth.ClientId, auth.Profile, "source_read", "ok", revision, new[] { id }); return Results.Ok(result);
    }
    catch (Exception ex) { return await HandleFailure(context, ex, auth, "source_read"); }
});

app.MapPost("/v1/drafts", async (HttpContext context) =>
{
    var auth = Authenticate(context); if (auth is null) return await AuthenticationFailure(context);
    if (!HasScope(auth, "draft")) return await Denied(context, auth, "draft", "forbidden");
    try
    {
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (!ValidIdempotencyKey(key)) { LogRequestFailure(context, "draft", "Idempotency-Key is missing or has an invalid format"); return await ErrorResult(context, 400, "invalid_request"); }
        var req = await ReadJson<DraftRequest>(context);
        if (req is null) { LogRequestFailure(context, "draft", "request body is empty or invalid"); return await ErrorResult(context, 400, "invalid_request"); }
        if (string.IsNullOrWhiteSpace(req.Title)) { LogRequestFailure(context, "draft", "title is required"); return await ErrorResult(context, 400, "invalid_request"); }
        if (string.IsNullOrWhiteSpace(req.Body) || Encoding.UTF8.GetByteCount(req.Body) > 64 * 1024) { LogRequestFailure(context, "draft", "body is required and must not exceed 65536 UTF-8 bytes"); return await ErrorResult(context, 400, "invalid_request"); }
        if (req.Citations is null || req.Citations.Length is < 1 or > 10) { LogRequestFailure(context, "draft", "citations must contain between 1 and 10 items"); return await ErrorResult(context, 400, "invalid_request"); }
        var digest = DraftDigest(req); var sourceIds = req.Citations.Select(x => x.SourceId).ToArray();
        using var clientGuard = clients.AcquireGuard(context.RequestAborted);
        using var guard = store.AcquirePolicyGuard(context.RequestAborted);
        if (!StillAuthorized(context, auth, "draft")) return await Revoked(context, auth, "draft");
        var revision = store.PolicyRevision;
        var dependencyHashes = ValidateDraftSources(req, auth.Profile);
        IdempotencyOperation operation;
        bool replay;
        try { operation = idempotency.Reserve(auth.ClientId, key, digest, sourceIds, dependencyHashes, out replay); }
        catch (InvalidOperationException) { return await Fail(context, auth, "draft", revision, 409, "idempotency_conflict"); }
        if (operation.DependencyHashes is not null && !operation.DependencyHashes.SequenceEqual(dependencyHashes, StringComparer.OrdinalIgnoreCase)) return await Fail(context, auth, "draft", revision, 404, "resource_unavailable");
        DraftReceipt receipt;
        if (operation.Receipt is not null) receipt = operation.Receipt;
        else
        {
            KnowledgeRecordView? saved = null;
            try { saved = store.ShowRecord(operation.RecordId); }
            catch (FileNotFoundException) { }
            if (saved is not null && saved.Metadata.OperationId != operation.OperationId) return await Fail(context, auth, "draft", revision, 409, "operation_recovery_required");
            if (saved is null)
            {
                try { saved = store.CreateAgentDraft(req.Title, req.Body, req.Citations.Select(x => (x.SourceId, x.StartLine, x.EndLine, x.Quote)).ToArray(), operation.RecordId, operation.OperationId); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return await HandleFailure(context, ex, auth, "draft"); }
            }
            operation = idempotency.Update(operation, "record_saved");
            receipt = new(operation.RecordId, "draft", operation.OperationId, revision);
            operation = idempotency.Update(operation, "record_saved", receipt);
        }
        audit.Write(RequestId(context), auth.ClientId, auth.Profile, "draft", replay ? "idempotent_replay" : "ok", revision, new[] { receipt.Id });
        idempotency.Update(operation, "completed", receipt);
        if (replay) context.Response.Headers["Idempotency-Replayed"] = "true";
        return Results.Created($"/v1/records/{receipt.Id}", new { id = receipt.Id, status = receipt.Status, operation_id = receipt.OperationId, policy_revision = receipt.PolicyRevision });
    }
    catch (Exception ex) { return await HandleFailure(context, ex, auth, "draft"); }
});

app.MapFallback(async (HttpContext context) => await WriteError(context, 404, "resource_unavailable"));

app.Run();

ClientCredential? Authenticate(HttpContext context)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length <= 7) return null;
    try { return clients.Authenticate(header[7..]); } catch { context.Items["auth_error"] = true; return null; }
}
async Task<IResult> AuthenticationFailure(HttpContext context) => context.Items.ContainsKey("auth_error")
    ? await ErrorResult(context, 503, "service_unavailable") : await ErrorResult(context, 401, "unauthorized");
bool StillAuthorized(HttpContext context, ClientCredential original, string scope)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length <= 7) return false;
    var current = clients.AuthenticateGuarded(header[7..]); return current is not null && current.ClientId == original.ClientId && current.Profile == original.Profile && HasScope(current, scope);
}
static bool HasScope(ClientCredential credential, string scope) => credential.Scopes.Contains(scope, StringComparer.Ordinal);
static string RequestId(HttpContext context) => (string)context.Items["request_id"]!;
long CurrentRevision() { try { return store.PolicyRevision; } catch { return 0; } }
async Task<IResult> Denied(HttpContext context, ClientCredential client, string operation, string error)
{
    try { audit.Write(RequestId(context), client.ClientId, client.Profile, operation, error, store.PolicyRevision); } catch { }
    return await ErrorResult(context, 403, error);
}
async Task<IResult> Revoked(HttpContext context, ClientCredential client, string operation)
{
    try { audit.Write(RequestId(context), client.ClientId, client.Profile, operation, "unauthorized", store.PolicyRevision); } catch { }
    return await ErrorResult(context, 401, "unauthorized");
}
async Task<IResult> Fail(HttpContext context, ClientCredential client, string operation, long revision, int status, string error)
{
    try { audit.Write(RequestId(context), client.ClientId, client.Profile, operation, error, revision); }
    catch { status = 503; error = "service_unavailable"; }
    return await ErrorResult(context, status, error);
}
async Task<IResult> HandleFailure(HttpContext context, Exception ex, ClientCredential client, string operation)
{
    var (status, error) = ex switch
    {
        FileNotFoundException => (404, "resource_unavailable"),
        BadHttpRequestException badRequest when badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge => (413, "request_too_large"),
        ArgumentException or JsonException => (400, "invalid_request"),
        InvalidOperationException when ex.Message == "idempotency_conflict" => (409, "idempotency_conflict"),
        InvalidOperationException => (503, "service_unavailable"),
        IOException when ex.Message == "policy_changed" => (409, "retry_required"),
        IOException when ex.Message == "retry_required" => (503, "retry_required"),
        IOException when ex.Message == "operation_recovery_required" => (409, "operation_recovery_required"),
        IOException when ex.Message == "search_session_limit" => (429, "rate_limited"),
        IOException => (503, "service_unavailable"),
        _ => (503, "service_unavailable")
    };
    if (status >= 500)
        app.Logger.LogError(ex, "Request {RequestId} operation {Operation} failed with {ErrorCode}", RequestId(context), operation, error);
    else
        app.Logger.LogWarning("Request {RequestId} operation {Operation} failed with {ErrorCode}: {ExceptionType}: {Reason}", RequestId(context), operation, error, ex.GetType().Name, ex.Message);
    return await Fail(context, client, operation, CurrentRevision(), status, error);
}
void LogRequestFailure(HttpContext context, string operation, string reason) =>
    app.Logger.LogWarning("Request {RequestId} operation {Operation} rejected: {Reason}", RequestId(context), operation, reason);
async Task<IResult> ErrorResult(HttpContext context, int status, string error) => Results.Json(new { error, request_id = RequestId(context) }, statusCode: status);
async Task WriteError(HttpContext context, int status, string error)
{
    context.Response.StatusCode = status; await context.Response.WriteAsJsonAsync(new { error, request_id = RequestId(context) });
}
async Task<T?> ReadJson<T>(HttpContext context)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(10));
    try { return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }, timeout.Token); }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { throw new ArgumentException("request_timeout"); }
}
string[] ValidateDraftSources(DraftRequest request, string profile)
{
    var hashes = new List<string>();
    foreach (var citation in request.Citations)
    {
        var metadata = store.Show(citation.SourceId).Record;
        if (profile == "external" && metadata.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        var (source, text) = store.ReadExtractedSource(citation.SourceId);
        if (profile == "external" && source.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        var lines = text.Split('\n');
        if (citation.StartLine < 1 || citation.EndLine < citation.StartLine || citation.EndLine > lines.Length || string.Join("\n", lines[(citation.StartLine - 1)..citation.EndLine]) != citation.Quote) throw new ArgumentException("Invalid citation");
        hashes.Add(source.ExtractedTextSha256!);
    }
    return hashes.ToArray();
}
static FileStream AcquireServerLock(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException ex) { throw new InvalidOperationException("Another agent server is already using this server state", ex); }
}
static bool ValidIdempotencyKey(string key) => key.Length is > 0 and <= 128 && key.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or ':');
static string DraftDigest(DraftRequest request)
{
    var normalized = new { title = request.Title.Normalize(NormalizationForm.FormC), body = request.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Normalize(NormalizationForm.FormC), citations = request.Citations.Select(x => new { source_id = x.SourceId, start_line = x.StartLine, end_line = x.EndLine, quote = x.Quote.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Normalize(NormalizationForm.FormC) }).ToArray() };
    return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized))).ToLowerInvariant();
}
static object CitationDto(AgentCitation c) => new { source_id = c.SourceId, origin_urls = c.OriginUrls, text_sha256 = c.TextSha256, start_line = c.StartLine, end_line = c.EndLine, text = c.Quote };
static string Snippet(string body, string query)
{
    var line = body.Split('\n').FirstOrDefault(x => x.Contains(query.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], StringComparison.OrdinalIgnoreCase)) ?? body;
    return line.Length <= 300 ? line : line[..300];
}
record SearchRequest(string Query, int Limit = 10, string? Cursor = null);
record CitationRequest(Guid SourceId, int StartLine, int EndLine, string Quote);
record DraftRequest(string Title, string Body, CitationRequest[] Citations);
