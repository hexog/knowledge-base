using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace KnowledgeBase.Core;

public sealed record AgentCitation(Guid SourceId, string TextSha256, int StartLine, int EndLine, string Quote);
public sealed record AgentRecord(Guid Id, string Version, string Type, string Kind, string Status, string Title, string Body, AgentCitation[] Citations, long PolicyRevision);
public sealed record AgentSearchPage(AgentRecord[] Results, string? Cursor, long PolicyRevision);

public sealed class AgentKnowledgeService
{
    private readonly KnowledgeBaseStore store;
    private const int MaxSessions = 128;
    private const int MaxCandidatesPerSession = 1000;
    private const int MaxSessionBytes = 32 * 1024 * 1024;
    private readonly object sessionsGate = new();
    private readonly Dictionary<string, SearchSession> sessions = new(StringComparer.Ordinal);
    private int sessionBytes;
    private sealed record SourceDependency(Guid SourceId, string TextSha256, int StartLine, int EndLine);
    private sealed record SearchCandidate(Guid Id, string Version, string Status, SourceDependency[] Dependencies);
    private sealed record SearchSession(SearchCandidate[] Candidates, string ClientId, string Profile, string ScopeDigest, string QueryDigest, long Revision, DateTimeOffset ExpiresAt, int EstimatedBytes, int NextOffset = 0);
    public AgentKnowledgeService(KnowledgeBaseStore store, byte[] cursorKey) { this.store = store; }

    public AgentSearchPage Search(string query, int limit, string? cursor, string profile)
        => Search(query, limit, cursor, profile, "anonymous", Array.Empty<string>());

    public AgentSearchPage Search(string query, int limit, string? cursor, string profile, string clientId, IReadOnlyCollection<string> scopes)
    {
        if (query.Length is < 1 or > 512) throw new ArgumentException("Invalid query");
        if (profile is not ("local" or "external")) throw new ArgumentException("Invalid profile");
        limit = Math.Clamp(limit, 1, 50);
        var now = DateTimeOffset.UtcNow;
        lock (sessionsGate) RemoveExpired(now);
        var queryDigest = Hash(query + "\n" + limit); var scopeDigest = Hash(string.Join("\n", scopes.Order(StringComparer.Ordinal)));
        SearchSession session; var offset = 0; string? consumedCursor = null;
        if (cursor is null)
        {
            var records = store.ListRecords().Where(x => termsMatch(x, query)).OrderBy(x => x.Metadata.Id);
            var resolved = new List<SearchCandidate>();
            foreach (var candidate in records)
            {
                AgentRecord? item;
                try
                {
                    item = Resolve(candidate, profile);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { continue; }
                if (item is null) continue;
                if (resolved.Count == MaxCandidatesPerSession) throw new IOException("search_session_limit");
                resolved.Add(new(item.Id, item.Version, item.Status, item.Citations.Select(c => new SourceDependency(c.SourceId, c.TextSha256, c.StartLine, c.EndLine)).ToArray()));
            }
            var encoded = JsonSerializer.SerializeToUtf8Bytes(resolved).Length + Encoding.UTF8.GetByteCount(clientId + profile + scopeDigest + queryDigest);
            if (encoded > MaxSessionBytes) throw new IOException("search_session_limit");
            session = new(resolved.ToArray(), clientId, profile, scopeDigest, queryDigest, store.PolicyRevision, now.AddMinutes(10), encoded);
        }
        else
        {
            lock (sessionsGate)
            {
                RemoveExpired(now);
                if (!sessions.TryGetValue(cursor, out session!)) throw new ArgumentException("Invalid cursor");
                if (session.ClientId != clientId || session.Profile != profile || session.ScopeDigest != scopeDigest || session.QueryDigest != queryDigest) throw new ArgumentException("Invalid cursor");
                if (session.Revision != store.PolicyRevision) { RemoveSession(cursor); throw new IOException("policy_changed"); }
                offset = session.NextOffset; consumedCursor = cursor; RemoveSession(cursor);
            }
        }
        var pageCandidates = session.Candidates.Skip(offset).Take(limit).ToArray();
        var page = new List<AgentRecord>(pageCandidates.Length);
        foreach (var expected in pageCandidates)
        {
            AgentRecord? current;
            try { current = Resolve(expected.Id, profile); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { current = null; }
            var dependencies = current?.Citations.Select(c => new SourceDependency(c.SourceId, c.TextSha256, c.StartLine, c.EndLine)).ToArray();
            if (current is null || current.Version != expected.Version || current.Status != expected.Status || !expected.Dependencies.SequenceEqual(dependencies))
                throw new IOException("policy_changed");
            page.Add(current);
        }
        var nextOffset = offset + pageCandidates.Length;
        string? next = null;
        if (nextOffset < session.Candidates.Length)
        {
            next = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            lock (sessionsGate)
            {
                RemoveExpired(DateTimeOffset.UtcNow);
                if ((consumedCursor is null && sessions.Count >= MaxSessions) || sessionBytes + session.EstimatedBytes > MaxSessionBytes) throw new IOException("search_session_limit");
                sessions[next] = session with { NextOffset = nextOffset }; sessionBytes += session.EstimatedBytes;
            }
        }
        return new(page.ToArray(), next, session.Revision);
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var token in sessions.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray()) RemoveSession(token);
    }
    private void RemoveSession(string token)
    {
        if (sessions.Remove(token, out var removed)) sessionBytes -= removed.EstimatedBytes;
    }
    private static bool termsMatch(KnowledgeRecordView x, string query) => query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(term => (x.Metadata.Title + "\n" + x.Body).Contains(term, StringComparison.OrdinalIgnoreCase));

    public AgentRecord GetRecord(Guid id, string profile) => Resolve(id, profile) ?? throw new FileNotFoundException("resource_unavailable");

    public (Guid SourceId, string Name, string Text, int EndLine, long Revision) GetSourceText(Guid id, int startLine, int endLine, string profile)
    {
        if (profile is not ("local" or "external")) throw new FileNotFoundException("resource_unavailable");
        var metadata = store.Show(id).Record;
        if (profile == "external" && metadata.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        var revision = store.PolicyRevision;
        var (source, text) = store.ReadExtractedSource(id);
        if (profile == "external" && source.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        var lines = text.Split('\n');
        if (startLine < 1 || endLine < startLine || endLine - startLine + 1 > 200) throw new ArgumentException("Invalid line range");
        var actualEndLine = Math.Min(endLine, lines.Length);
        var content = startLine > actualEndLine ? "" : string.Join("\n", lines[(startLine - 1)..actualEndLine]);
        if (Encoding.UTF8.GetByteCount(content) > 64 * 1024) throw new ArgumentException("Source range exceeds limit");
        if (revision != store.PolicyRevision) throw new IOException("policy_changed");
        return (id, source.OriginalName, content, actualEndLine, revision);
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
        var record = store.ListRecords().SingleOrDefault(x => x.Metadata.Id == id);
        if (record is null) return null;
        return Resolve(record, profile);
    }

    private AgentRecord? Resolve(KnowledgeRecordView record, string profile)
    {
        if (profile is not ("local" or "external")) return null;
        var revision = store.PolicyRevision;
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
