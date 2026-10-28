using System.Text;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace KnowledgeBase.Core;

public sealed record AgentCitation(Guid SourceId, string TextSha256, int StartLine, int EndLine, string Quote);
public sealed record AgentRecord(Guid Id, string Version, string Type, string Kind, string Status, string Title, string Body, AgentCitation[] Citations, long PolicyRevision);
public sealed record AgentSearchPage(AgentRecord[] Results, string? Cursor, long PolicyRevision);

public sealed class AgentKnowledgeService
{
    private readonly KnowledgeBaseStore store;
    private readonly ConcurrentDictionary<string, SearchSession> sessions = new(StringComparer.Ordinal);
    private sealed record SearchSession(AgentRecord[] Results, string ClientId, string Profile, string ScopeDigest, string QueryDigest, long Revision, DateTimeOffset ExpiresAt);
    public AgentKnowledgeService(KnowledgeBaseStore store, byte[] cursorKey) { this.store = store; }

    public AgentSearchPage Search(string query, int limit, string? cursor, string profile)
        => Search(query, limit, cursor, profile, "anonymous", Array.Empty<string>());

    public AgentSearchPage Search(string query, int limit, string? cursor, string profile, string clientId, IReadOnlyCollection<string> scopes)
    {
        if (query.Length is < 1 or > 512) throw new ArgumentException("Invalid query");
        if (profile is not ("local" or "external")) throw new ArgumentException("Invalid profile");
        limit = Math.Clamp(limit, 1, 50);
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in sessions.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray()) { sessions.TryRemove(expired, out _); cursorPositions.TryRemove(expired, out _); }
        var queryDigest = Hash(query + "\n" + limit); var scopeDigest = Hash(string.Join("\n", scopes.Order(StringComparer.Ordinal)));
        SearchSession session; var offset = 0;
        if (cursor is null)
        {
            var candidates = store.ListRecords().Where(x => termsMatch(x, query)).OrderBy(x => x.Metadata.Id).ToArray();
            var resolved = new List<AgentRecord>();
            foreach (var candidate in candidates)
            {
                try { var item = Resolve(candidate.Metadata.Id, profile); if (item is not null) resolved.Add(item); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
            if (sessions.Count >= 500) throw new IOException("search_session_limit");
            session = new(resolved.ToArray(), clientId, profile, scopeDigest, queryDigest, store.PolicyRevision, now.AddMinutes(10));
        }
        else
        {
            if (!sessions.TryGetValue(cursor, out var found)) throw new ArgumentException("Invalid cursor");
            session = found;
            if (session.ExpiresAt <= now || session.ClientId != clientId || session.Profile != profile || session.ScopeDigest != scopeDigest || session.QueryDigest != queryDigest) throw new ArgumentException("Invalid cursor");
            if (session.Revision != store.PolicyRevision) throw new IOException("policy_changed");
            if (!cursorPositions.TryGetValue(cursor, out offset) || !sessions.TryRemove(cursor, out _)) throw new ArgumentException("Invalid cursor");
            cursorPositions.TryRemove(cursor, out _);
        }
        var page = session.Results.Skip(offset).Take(limit).ToArray(); var nextOffset = offset + page.Length;
        string? next = null;
        if (nextOffset < session.Results.Length)
        {
            next = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (!sessions.TryAdd(next, session) || !cursorPositions.TryAdd(next, nextOffset)) throw new IOException("search_session_limit");
        }
        return new(page, next, session.Revision);
    }

    private readonly ConcurrentDictionary<string, int> cursorPositions = new(StringComparer.Ordinal);
    private static bool termsMatch(KnowledgeRecordView x, string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(term => (x.Metadata.Title + "\n" + x.Body).Contains(term, StringComparison.OrdinalIgnoreCase));

    public AgentRecord GetRecord(Guid id, string profile) => Resolve(id, profile) ?? throw new FileNotFoundException("resource_unavailable");

    public (Guid SourceId, string Name, string Text, long Revision) GetSourceText(Guid id, int startLine, int endLine, string profile)
    {
        if (profile is not ("local" or "external")) throw new FileNotFoundException("resource_unavailable");
        var metadata = store.Show(id).Record;
        if (profile == "external" && metadata.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        var revision = store.PolicyRevision;
        var (source, text) = store.ReadExtractedSource(id);
        if (profile == "external" && source.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        var lines = text.Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length || endLine - startLine + 1 > 200) throw new ArgumentException("Invalid line range");
        var content = string.Join("\n", lines[(startLine - 1)..endLine]);
        if (Encoding.UTF8.GetByteCount(content) > 64 * 1024) throw new ArgumentException("Source range exceeds limit");
        if (revision != store.PolicyRevision) throw new IOException("policy_changed");
        return (id, source.OriginalName, content, revision);
    }

    public AgentRecord CreateDraft(string title, string body, IReadOnlyList<(Guid SourceId, int StartLine, int EndLine, string Quote)> citations, string profile)
    {
        if (profile is not ("local" or "external") || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body) || Encoding.UTF8.GetByteCount(body) > 64 * 1024 || citations.Count is < 1 or > 10)
            throw new ArgumentException("Invalid draft");
        var before = store.PolicyRevision;
        foreach (var citation in citations)
        {
            var (source, _) = store.ReadExtractedSource(citation.SourceId);
            if (profile == "external" && source.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        }
        var created = store.CreateAgentDraft(title, body, citations);
        if (before != store.PolicyRevision) throw new IOException("policy_changed");
        return ToDto(created, store.PolicyRevision);
    }

    private AgentRecord? Resolve(Guid id, string profile)
    {
        if (profile is not ("local" or "external")) return null;
        var revision = store.PolicyRevision;
        var record = store.ListRecords().SingleOrDefault(x => x.Metadata.Id == id);
        if (record is null) return null;
        if (profile == "external" && (record.Metadata.Status != "reviewed" || record.StaleReason is not null || store.EffectiveAccess(record.Metadata) != "external_allowed")) return null;
        if (profile == "external" && record.Metadata.SourceRefs.Length == 0) return null;
        var citations = new List<AgentCitation>();
        if (record.Metadata.SourceRefs.Length > 10 || Encoding.UTF8.GetByteCount(record.Body) > 64 * 1024) return null;
        var citedBytes = 0;
        foreach (var reference in record.Metadata.SourceRefs)
        {
            var (source, text) = store.ReadExtractedSource(reference.SourceId);
            if (profile == "external" && source.Access != "external_allowed") return null;
            var lines = text.Split('\n');
            if (reference.EndLine > lines.Length || string.Join("\n", lines[(reference.StartLine - 1)..reference.EndLine]) != reference.Quote || !string.Equals(reference.TextSha256, source.ExtractedTextSha256, StringComparison.OrdinalIgnoreCase)) return null;
            citedBytes += Encoding.UTF8.GetByteCount(reference.Quote);
            if (citedBytes > 64 * 1024) return null;
            citations.Add(new(reference.SourceId, reference.TextSha256, reference.StartLine, reference.EndLine, reference.Quote));
        }
        if (revision != store.PolicyRevision) throw new IOException("policy_changed");
        return new(record.Metadata.Id, KnowledgeRepository.ContentHash(record.Metadata, record.Body), record.Metadata.Type, record.Metadata.Kind,
            record.Metadata.Status, record.Metadata.Title, record.Body, citations.ToArray(), revision);
    }

    private AgentRecord ToDto(KnowledgeRecordView view, long revision) => new(view.Metadata.Id, KnowledgeRepository.ContentHash(view.Metadata, view.Body), view.Metadata.Type,
        view.Metadata.Kind, view.Metadata.Status, view.Metadata.Title, view.Body,
        view.Metadata.SourceRefs.Select(x => new AgentCitation(x.SourceId, x.TextSha256, x.StartLine, x.EndLine, x.Quote)).ToArray(), revision);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
