using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeBase.Core;

public sealed record ClientCredential(string ClientId, string Profile, string[] Scopes, string TokenSha256, bool Revoked = false);
public sealed record IssuedClient(string ClientId, string Profile, string[] Scopes, string Token);

public sealed class ClientRegistry
{
    private readonly string path;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public ClientRegistry(string path) { this.path = Path.GetFullPath(path); }
    public IssuedClient Create(string profile, IEnumerable<string> scopes)
    {
        if (profile is not ("local" or "external")) throw new ArgumentException("Profile must be local or external");
        var allowed = new HashSet<string>(new[] { "search", "read", "draft" }, StringComparer.Ordinal);
        var scopeSet = scopes.Distinct(StringComparer.Ordinal).ToArray();
        if (scopeSet.Length == 0 || scopeSet.Any(x => !allowed.Contains(x))) throw new ArgumentException("Scopes must be search, read, and/or draft");
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var id = Guid.NewGuid().ToString("N"); var item = new ClientCredential(id, profile, scopeSet, Hash(token));
        lock (gate) { var all = Load().Where(x => x.ClientId != id).Append(item).ToArray(); Save(all); }
        return new(id, profile, scopeSet, token);
    }
    public bool Revoke(string id)
    {
        lock (gate)
        {
            var all = Load(); var found = all.Any(x => x.ClientId == id && !x.Revoked);
            Save(all.Select(x => x.ClientId == id ? x with { Revoked = true } : x).ToArray()); return found;
        }
    }
    public ClientCredential? Authenticate(string token)
    {
        var hash = Hash(token);
        lock (gate) return Load().FirstOrDefault(x => !x.Revoked && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(x.TokenSha256), Convert.FromHexString(hash)));
    }
    private ClientCredential[] Load()
    {
        if (!File.Exists(path)) return Array.Empty<ClientCredential>();
        return JsonSerializer.Deserialize<ClientCredential[]>(File.ReadAllText(path), Json) ?? Array.Empty<ClientCredential>();
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
                    catch { return false; }
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
