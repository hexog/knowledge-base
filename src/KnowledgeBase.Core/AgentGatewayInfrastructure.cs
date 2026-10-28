using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeBase.Core;

public sealed record ClientCredential(string ClientId, string Profile, string[] Scopes, string TokenSha256, bool Revoked = false);
public sealed record IssuedClient(string ClientId, string Profile, string[] Scopes, string Token);
public sealed record DraftReceipt(Guid Id, string Status, string OperationId, long PolicyRevision);
public sealed record IdempotencyOperation(string ClientId, string Key, string Digest, Guid RecordId, string OperationId,
    string Phase, DraftReceipt? Receipt, Guid[] SourceIds, string[]? DependencyHashes = null);

public sealed class IdempotencyStore
{
    private const long MaxBytes = 50L * 1024 * 1024;
    private readonly string path;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public IdempotencyStore(string directory) { Directory.CreateDirectory(directory); path = Path.Combine(directory, "idempotency.json"); _ = Load(); }
    public IdempotencyOperation Reserve(string clientId, string key, string digest, Guid[] sourceIds, string[] dependencyHashes, out bool replay)
    {
        lock (gate)
        {
            var all = Load(); var existing = all.SingleOrDefault(x => x.ClientId == clientId && x.Key == key);
            if (existing is not null)
            {
                if (!string.Equals(existing.Digest, digest, StringComparison.Ordinal)) throw new InvalidOperationException("idempotency_conflict");
                replay = true;
                return existing;
            }
            if (new FileInfo(path).Exists && new FileInfo(path).Length >= MaxBytes) throw new IOException("idempotency_store_full");
            var operation = new IdempotencyOperation(clientId, key, digest, Guid.NewGuid(), Guid.NewGuid().ToString("N"), "reserved", null, sourceIds, dependencyHashes);
            Save(all.Append(operation).ToArray()); replay = false; return operation;
        }
    }
    public IdempotencyOperation Update(IdempotencyOperation operation, string phase, DraftReceipt? receipt = null)
    {
        lock (gate)
        {
            var all = Load(); var index = Array.FindIndex(all, x => x.ClientId == operation.ClientId && x.Key == operation.Key);
            if (index < 0 || all[index].Digest != operation.Digest) throw new InvalidDataException("Idempotency operation disappeared or changed");
            var updated = all[index] with { Phase = phase, Receipt = receipt ?? all[index].Receipt };
            all[index] = updated; Save(all); return updated;
        }
    }
    public IdempotencyOperation[] Operations { get { lock (gate) return Load(); } }
    private IdempotencyOperation[] Load()
    {
        if (!File.Exists(path)) return Array.Empty<IdempotencyOperation>();
        try
        {
            var operations = JsonSerializer.Deserialize<IdempotencyOperation[]>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Idempotency state is empty");
            if (operations.Any(x => x is null || string.IsNullOrWhiteSpace(x.ClientId) || string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.Digest) || x.RecordId == Guid.Empty || string.IsNullOrWhiteSpace(x.OperationId) || x.Phase is not ("reserved" or "record_saved" or "completed" or "failed_validation") || x.SourceIds is null) || operations.Select(x => (x.ClientId, x.Key)).Distinct().Count() != operations.Length)
                throw new InvalidDataException("Idempotency state contains invalid operations");
            return operations;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { throw new InvalidDataException("Idempotency state is invalid", ex); }
    }
    private void Save(IdempotencyOperation[] items)
    {
        var content = JsonSerializer.Serialize(items, Json) + "\n";
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes) throw new IOException("idempotency_store_full");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(content); writer.Flush(); stream.Flush(true); }
            ClientRegistry.Restrict(temp); File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class ClientRegistry
{
    private readonly string path;
    private readonly object gate = new();
    private readonly string lockPath;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public ClientRegistry(string path) { this.path = Path.GetFullPath(path); lockPath = this.path + ".lock"; }
    public IssuedClient Create(string profile, IEnumerable<string> scopes)
    {
        if (profile is not ("local" or "external")) throw new ArgumentException("Profile must be local or external");
        var allowed = new HashSet<string>(new[] { "search", "read", "draft" }, StringComparer.Ordinal);
        var scopeSet = scopes.Distinct(StringComparer.Ordinal).ToArray();
        if (scopeSet.Length == 0 || scopeSet.Any(x => !allowed.Contains(x))) throw new ArgumentException("Scopes must be search, read, and/or draft");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var id = Guid.NewGuid().ToString("N"); var item = new ClientCredential(id, profile, scopeSet, Hash(token));
        lock (gate) using (AcquireProcessLock()) { var all = Load().Where(x => x.ClientId != id).Append(item).ToArray(); Save(all); }
        return new(id, profile, scopeSet, token);
    }
    public bool Revoke(string id)
    {
        lock (gate) using (AcquireProcessLock())
        {
            var all = Load(); var found = all.Any(x => x.ClientId == id && !x.Revoked);
            Save(all.Select(x => x.ClientId == id ? x with { Revoked = true } : x).ToArray()); return found;
        }
    }
    public ClientCredential? Authenticate(string token)
    {
        var hash = Hash(token);
        lock (gate) using (AcquireProcessLock()) return Load().FirstOrDefault(x => !x.Revoked && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(x.TokenSha256), Convert.FromHexString(hash)));
    }
    public IDisposable AcquireGuard()
        => AcquireGuard(CancellationToken.None);
    public IDisposable AcquireGuard(CancellationToken cancellationToken)
    {
        Monitor.Enter(gate);
        try { return new RegistryGuard(gate, AcquireProcessLock(cancellationToken)); }
        catch { Monitor.Exit(gate); throw; }
    }
    private sealed class RegistryGuard : IDisposable
    {
        private readonly object gate; private readonly FileStream stream;
        public RegistryGuard(object gate, FileStream stream) { this.gate = gate; this.stream = stream; }
        public void Dispose() { stream.Dispose(); Monitor.Exit(gate); }
    }
    public ClientCredential? AuthenticateGuarded(string token)
    {
        if (!Monitor.IsEntered(gate)) throw new InvalidOperationException("Registry guard is required");
        var hash = Hash(token); return Load().FirstOrDefault(x => !x.Revoked && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(x.TokenSha256), Convert.FromHexString(hash)));
    }
    private FileStream AcquireProcessLock(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { cancellationToken.WaitHandle.WaitOne(25); cancellationToken.ThrowIfCancellationRequested(); }
            catch (IOException ex) { throw new IOException("Client registry is busy", ex); }
        }
    }
    private ClientCredential[] Load()
    {
        if (!File.Exists(path)) return Array.Empty<ClientCredential>();
        try
        {
            var items = JsonSerializer.Deserialize<ClientCredential[]>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Client registry is empty or invalid");
            if (items.Any(x => x is null || string.IsNullOrWhiteSpace(x.ClientId) || x.Profile is not ("local" or "external") || x.Scopes is null || x.Scopes.Length == 0 || x.Scopes.Any(s => s is not ("search" or "read" or "draft")) || x.Scopes.Distinct(StringComparer.Ordinal).Count() != x.Scopes.Length || x.TokenSha256 is null || x.TokenSha256.Length != 64 || x.TokenSha256.Any(c => !Uri.IsHexDigit(c))) || items.Select(x => x.ClientId).Distinct(StringComparer.Ordinal).Count() != items.Length || items.Select(x => x.TokenSha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length) throw new InvalidDataException("Client registry contains invalid credentials");
            return items;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { throw new InvalidDataException("Client registry is invalid", ex); }
    }
    private void Save(ClientCredential[] items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(items, Json) + "\n", new UTF8Encoding(false)); Restrict(temp); File.Move(temp, path, true);
    }
    public static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed class AuditWriter
{
    private const long MaxBytes = 100L * 1024 * 1024;
    private readonly string path;
    private readonly object gate = new();
    private DateTimeOffset pruneAfter = DateTimeOffset.MinValue;
    public AuditWriter(string directory) { Directory.CreateDirectory(directory); path = Path.Combine(directory, "agent-audit.jsonl"); }
    public void Write(string requestId, string clientId, string profile, string operation, string result, long revision, IEnumerable<Guid>? resourceIds = null)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= pruneAfter && File.Exists(path))
            {
                var cutoff = now.AddDays(-30);
                var prior = File.ReadLines(path).Where(line =>
                {
                    try { using var doc = JsonDocument.Parse(line); return doc.RootElement.GetProperty("at").GetDateTimeOffset() >= cutoff; }
                    catch (Exception ex) { throw new InvalidDataException("Audit log is corrupt", ex); }
                }).ToArray();
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllText(temp, string.Join("\n", prior) + (prior.Length == 0 ? "" : "\n"), new UTF8Encoding(false)); ClientRegistry.Restrict(temp); File.Move(temp, path, true);
                pruneAfter = now.AddHours(24);
            }
            var entry = JsonSerializer.Serialize(new { at = now, request_id = requestId, client_id = clientId, profile, operation, result, policy_revision = revision, resource_ids = resourceIds?.ToArray() }) + "\n";
            var bytes = Encoding.UTF8.GetBytes(entry); var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size + bytes.Length > MaxBytes) throw new IOException("audit_limit_reached");
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read); stream.Write(bytes); stream.Flush(true);
            ClientRegistry.Restrict(path);
        }
    }
}
