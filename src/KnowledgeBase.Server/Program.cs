using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnowledgeBase.Core;

var database = Environment.GetEnvironmentVariable("KB_DATABASE_PATH") ?? throw new InvalidOperationException("Set KB_DATABASE_PATH");
var state = Environment.GetEnvironmentVariable("KB_SERVER_STATE_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kb", "server");
var registryPath = Environment.GetEnvironmentVariable("KB_CLIENT_REGISTRY_PATH") ?? Path.Combine(Path.GetDirectoryName(state)!, "clients.json");
Directory.CreateDirectory(state);
var store = new KnowledgeBaseStore(database); store.Initialize();
if (store.SchemaVersion != "3") throw new InvalidOperationException("Run 'kb migrate --to 3' before starting the agent server");
store.RecoverServerState();
var cursorKeyPath = Path.Combine(state, "cursor.key");
if (!File.Exists(cursorKeyPath)) { File.WriteAllBytes(cursorKeyPath, RandomNumberGenerator.GetBytes(32)); ClientRegistry.Restrict(cursorKeyPath); }
ClientRegistry.Restrict(cursorKeyPath);
var service = new AgentKnowledgeService(store, File.ReadAllBytes(cursorKeyPath));
var clients = new ClientRegistry(registryPath); var audit = new AuditWriter(state);
var draftsPath = Path.Combine(state, "idempotency.json");
var idempotency = new ConcurrentDictionary<string, (string Hash, Guid RecordId)>(LoadIdempotency(draftsPath));
var idempotencyGate = new SemaphoreSlim(1, 1);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => { options.Listen(System.Net.IPAddress.Loopback, int.TryParse(Environment.GetEnvironmentVariable("KB_SERVER_PORT"), out var port) ? port : 8765); options.Limits.MaxRequestBodySize = 1024 * 1024; });
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "forbidden" }); return; }
    if (context.Request.ContentLength > 1024 * 1024) { context.Response.StatusCode = 413; await context.Response.WriteAsJsonAsync(new { error = "request_too_large" }); return; }
    if (context.Request.Path.StartsWithSegments("/v1"))
    {
        using var policyGuard = store.AcquirePolicyGuard();
        await next();
        return;
    }
    await next();
});

app.MapPost("/v1/search", async (HttpContext context) =>
{
    var auth = Authenticate(context, "search"); if (auth is null) return Results.Unauthorized();
    var requestId = Guid.NewGuid().ToString("N"); var startRevision = store.PolicyRevision;
    try
    {
        var req = await ReadJson<SearchRequest>(context);
        if (req is null || string.IsNullOrWhiteSpace(req.Query) || req.Query.Length > 512 || req.Limit is < 1 or > 50) return Results.BadRequest(new { error = "invalid_request" });
        var page = service.Search(req.Query, req.Limit, req.Cursor, auth.Profile);
        if (startRevision != store.PolicyRevision) return Results.StatusCode(409);
        var results = page.Results.Select(x => new { x.Id, x.Version, x.Type, x.Kind, x.Status, x.Title, snippet = Snippet(x.Body, req.Query), citations = x.Citations.Select(c => new { source_id = c.SourceId, text_sha256 = c.TextSha256, start_line = c.StartLine, end_line = c.EndLine, text = c.Quote }) });
        audit.Write(requestId, auth.ClientId, auth.Profile, "search", "ok", startRevision, page.Results.Select(x => x.Id));
        return Results.Ok(new { results, cursor = page.Cursor, policy_revision = startRevision });
    }
    catch (Exception ex) { return Failure(ex, audit, requestId, auth, "search", startRevision); }
});

app.MapGet("/v1/records/{id:guid}", (HttpContext context, Guid id) =>
{
    var auth = Authenticate(context, "read"); if (auth is null) return Results.Unauthorized(); var requestId = Guid.NewGuid().ToString("N"); var revision = store.PolicyRevision;
    try
    {
        var record = service.GetRecord(id, auth.Profile);
        if (revision != store.PolicyRevision) return Results.StatusCode(409);
        audit.Write(requestId, auth.ClientId, auth.Profile, "record_read", "ok", revision, new[] { id });
        return Results.Ok(new { record.Id, record.Version, record.Type, record.Kind, record.Status, record.Title, record.Body, citations = record.Citations.Select(c => new { source_id = c.SourceId, text_sha256 = c.TextSha256, start_line = c.StartLine, end_line = c.EndLine, text = c.Quote }), policy_revision = revision });
    }
    catch (Exception ex) { return Failure(ex, audit, requestId, auth, "record_read", revision); }
});

app.MapGet("/v1/sources/{id:guid}/text", (HttpContext context, Guid id, int start_line, int end_line) =>
{
    var auth = Authenticate(context, "read"); if (auth is null) return Results.Unauthorized(); var requestId = Guid.NewGuid().ToString("N"); var revision = store.PolicyRevision;
    try
    {
        var source = service.GetSourceText(id, start_line, end_line, auth.Profile);
        if (revision != store.PolicyRevision) return Results.StatusCode(409);
        audit.Write(requestId, auth.ClientId, auth.Profile, "source_read", "ok", revision, new[] { id });
        return Results.Ok(new { source_id = source.SourceId, name = source.Name, start_line, end_line, text = source.Text, policy_revision = revision });
    }
    catch (Exception ex) { return Failure(ex, audit, requestId, auth, "source_read", revision); }
});

app.MapPost("/v1/drafts", async (HttpContext context) =>
{
    var auth = Authenticate(context, "draft"); if (auth is null) return Results.Unauthorized(); var requestId = Guid.NewGuid().ToString("N"); var revision = store.PolicyRevision;
    try
    {
        await idempotencyGate.WaitAsync(context.RequestAborted);
        try
        {
        var key = context.Request.Headers["Idempotency-Key"].ToString(); if (key.Length is < 1 or > 128) return Results.BadRequest(new { error = "invalid_request" });
        var req = await ReadJson<DraftRequest>(context);
        if (req is null || string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body) || Encoding.UTF8.GetByteCount(req.Body) > 64 * 1024 || req.Citations is null || req.Citations.Length is < 1 or > 10) return Results.BadRequest(new { error = "invalid_request" });
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(req))).ToLowerInvariant();
        if (idempotency.TryGetValue(auth.ClientId + ":" + key, out var prior))
        {
            if (prior.Hash != digest) return Results.Conflict(new { error = "idempotency_conflict" });
            var existing = service.GetRecord(prior.RecordId, auth.Profile); audit.Write(requestId, auth.ClientId, auth.Profile, "draft", "idempotent_replay", revision, new[] { existing.Id });
            return Results.Ok(new { id = existing.Id, status = existing.Status, policy_revision = revision });
        }
        var created = service.CreateDraft(req.Title, req.Body, req.Citations.Select(x => (x.SourceId, x.StartLine, x.EndLine, x.Quote)).ToArray(), auth.Profile);
        if (revision != store.PolicyRevision) return Results.StatusCode(409);
        if (!idempotency.TryAdd(auth.ClientId + ":" + key, (digest, created.Id))) return Results.Conflict(new { error = "idempotency_conflict" });
        SaveIdempotency(draftsPath, idempotency);
        audit.Write(requestId, auth.ClientId, auth.Profile, "draft", "ok", revision, new[] { created.Id });
        return Results.Created($"/v1/records/{created.Id}", new { id = created.Id, status = "draft", policy_revision = revision });
        }
        finally { idempotencyGate.Release(); }
    }
    catch (Exception ex) { return Failure(ex, audit, requestId, auth, "draft", revision); }
});
app.Run();

ClientCredential? Authenticate(HttpContext context, string scope)
{
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length <= 7) return null;
    var client = clients.Authenticate(header[7..]); return client is not null && client.Scopes.Contains(scope, StringComparer.Ordinal) ? client : null;
}
IResult Failure(Exception ex, AuditWriter writer, string requestId, ClientCredential client, string operation, long revision)
{
    var status = ex is FileNotFoundException ? 404 : ex is ArgumentException or JsonException ? 400 : ex.Message == "policy_changed" ? 409 : 500;
    var error = status switch { 404 => "resource_unavailable", 400 => "invalid_request", 409 => "retry_required", _ => "service_unavailable" };
    try { writer.Write(requestId, client.ClientId, client.Profile, operation, error, revision); }
    catch { return Results.Json(new { error = "service_unavailable" }, statusCode: 503); }
    return Results.Json(new { error }, statusCode: status);
}
async Task<T?> ReadJson<T>(HttpContext context)
{
    if (context.Request.ContentLength is > 1024 * 1024) throw new ArgumentException("request_too_large");
    return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 32, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }, context.RequestAborted);
}
static string Snippet(string body, string query)
{
    var line = body.Split('\n').FirstOrDefault(x => x.Contains(query.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], StringComparison.OrdinalIgnoreCase)) ?? body;
    return line.Length <= 300 ? line : line[..300];
}
static Dictionary<string, (string Hash, Guid RecordId)> LoadIdempotency(string path)
{
    if (!File.Exists(path)) return new();
    try { return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path))?.ToDictionary(x => x.Key, x => (x.Value.Hash, x.Value.RecordId)) ?? new(); } catch { throw new InvalidDataException("Idempotency state is invalid"); }
}
static void SaveIdempotency(string path, ConcurrentDictionary<string, (string Hash, Guid RecordId)> values)
{
    var data = values.ToDictionary(x => x.Key, x => new Entry(x.Value.Hash, x.Value.RecordId)); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
    File.WriteAllText(temp, JsonSerializer.Serialize(data), new UTF8Encoding(false)); ClientRegistry.Restrict(temp); File.Move(temp, path, true);
}
record Entry(string Hash, Guid RecordId);
record SearchRequest(string Query, int Limit = 10, string? Cursor = null);
record CitationRequest(Guid SourceId, int StartLine, int EndLine, string Quote);
record DraftRequest(string Title, string Body, CitationRequest[] Citations);
