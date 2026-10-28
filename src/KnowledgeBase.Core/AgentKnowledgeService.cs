using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnowledgeBase.Core;

public sealed record AgentCitation(Guid SourceId, string TextSha256, int StartLine, int EndLine, string Quote);
public sealed record AgentRecord(Guid Id, string Version, string Type, string Kind, string Status, string Title, string Body, AgentCitation[] Citations, long PolicyRevision);
public sealed record AgentSearchPage(AgentRecord[] Results, string? Cursor, long PolicyRevision);

public sealed class AgentKnowledgeService
{
    private readonly KnowledgeBaseStore store;
    private readonly byte[] cursorKey;
    public AgentKnowledgeService(KnowledgeBaseStore store, byte[] cursorKey) { this.store = store; this.cursorKey = cursorKey; }

    public AgentSearchPage Search(string query, int limit, string? cursor, string profile)
    {
        if (query.Length is < 1 or > 512) throw new ArgumentException("Invalid query");
        if (profile is not ("local" or "external")) throw new ArgumentException("Invalid profile");
        limit = Math.Clamp(limit, 1, 50);
        var offset = cursor is null ? 0 : DecodeCursor(cursor, query);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = store.ListRecords().Where(x => terms.All(term => (x.Metadata.Title + "\n" + x.Body).Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Metadata.Id).ToArray();
        var results = new List<AgentRecord>(limit); var position = offset;
        for (; position < candidates.Length && results.Count < limit; position++)
        {
            try { var item = Resolve(candidates[position].Metadata.Id, profile); if (item is not null) results.Add(item); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        var next = position < candidates.Length ? EncodeCursor(position, query) : null;
        return new(results.ToArray(), next, store.PolicyRevision);
    }

    public AgentRecord GetRecord(Guid id, string profile) => Resolve(id, profile) ?? throw new FileNotFoundException("resource_unavailable");

    public (Guid SourceId, string Name, string Text, long Revision) GetSourceText(Guid id, int startLine, int endLine, string profile)
    {
        var revision = store.PolicyRevision;
        var (source, text) = store.ReadExtractedSource(id);
        if (profile == "external" && source.Access != "external_allowed") throw new FileNotFoundException("resource_unavailable");
        if (profile is not ("local" or "external")) throw new FileNotFoundException("resource_unavailable");
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

    private string EncodeCursor(int offset, string query)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { offset, queryHash = Hash(query) })));
        var signature = Convert.ToBase64String(HMACSHA256.HashData(cursorKey, Encoding.UTF8.GetBytes(payload)));
        return payload + "." + signature;
    }
    private int DecodeCursor(string cursor, string query)
    {
        var parts = cursor.Split('.'); if (parts.Length != 2) throw new ArgumentException("Invalid cursor");
        var signature = HMACSHA256.HashData(cursorKey, Encoding.UTF8.GetBytes(parts[0]));
        if (!CryptographicOperations.FixedTimeEquals(signature, Convert.FromBase64String(parts[1]))) throw new ArgumentException("Invalid cursor");
        using var payload = JsonDocument.Parse(Convert.FromBase64String(parts[0]));
        if (payload.RootElement.GetProperty("queryHash").GetString() != Hash(query)) throw new ArgumentException("Invalid cursor");
        var offset = payload.RootElement.GetProperty("offset").GetInt32(); if (offset < 0) throw new ArgumentException("Invalid cursor"); return offset;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
