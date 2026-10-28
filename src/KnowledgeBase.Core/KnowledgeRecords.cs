using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KnowledgeBase.Core;

public sealed record KnowledgeSourceRef(Guid SourceId, string TextSha256, int StartLine, int EndLine, string Quote);
public sealed record RecordTransition(string Status, DateTimeOffset At, string? Reason = null);
public sealed record KnowledgeRecord(string SchemaVersion, Guid Id, string Type, string Kind, string Status, string Title,
    string? Project, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string Access, KnowledgeSourceRef[] SourceRefs, RecordTransition[] History, DateTimeOffset? ReviewedAt = null,
    KnowledgeSourceRef[]? ReviewedSources = null, string? RejectionReason = null, string[]? RelatedRecords = null);
public sealed record KnowledgeRecordView(KnowledgeRecord Metadata, string Body, string Path);
public sealed class KnowledgeSourceRefYaml
{
    public Guid SourceId { get; set; } public string TextSha256 { get; set; } = ""; public int StartLine { get; set; } public int EndLine { get; set; } public string Quote { get; set; } = "";
    public KnowledgeSourceRef ToRef() => new(SourceId, TextSha256, StartLine, EndLine, Quote);
    public static KnowledgeSourceRefYaml From(KnowledgeSourceRef x) => new() { SourceId = x.SourceId, TextSha256 = x.TextSha256, StartLine = x.StartLine, EndLine = x.EndLine, Quote = x.Quote };
}
public sealed class RecordTransitionYaml
{
    public string Status { get; set; } = ""; public string At { get; set; } = ""; public string? Reason { get; set; }
    public RecordTransition ToTransition() => new(Status, DateTimeOffset.Parse(At), Reason);
    public static RecordTransitionYaml From(RecordTransition x) => new() { Status = x.Status, At = x.At.ToString("O"), Reason = x.Reason };
}
public sealed class KnowledgeRecordYaml
{
    public string SchemaVersion { get; set; } = "1"; public Guid Id { get; set; } public string Type { get; set; } = "fact"; public string Kind { get; set; } = "source_claim";
    public string Status { get; set; } = "draft"; public string Title { get; set; } = ""; public string? Project { get; set; }
    public string? ValidFrom { get; set; } public string? ValidTo { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = "";
    public string Access { get; set; } = "unclassified"; public KnowledgeSourceRefYaml[] SourceRefs { get; set; } = Array.Empty<KnowledgeSourceRefYaml>();
    public RecordTransitionYaml[] History { get; set; } = Array.Empty<RecordTransitionYaml>(); public string? ReviewedAt { get; set; }
    public KnowledgeSourceRefYaml[]? ReviewedSources { get; set; } public string? RejectionReason { get; set; } public string[]? RelatedRecords { get; set; }
    public static KnowledgeRecordYaml From(KnowledgeRecord m) => new() { SchemaVersion = m.SchemaVersion, Id = m.Id, Type = m.Type, Kind = m.Kind,
        Status = m.Status, Title = m.Title, Project = m.Project, ValidFrom = m.ValidFrom?.ToString("O"), ValidTo = m.ValidTo?.ToString("O"),
        CreatedAt = m.CreatedAt.ToString("O"), UpdatedAt = m.UpdatedAt.ToString("O"), Access = m.Access, SourceRefs = m.SourceRefs.Select(KnowledgeSourceRefYaml.From).ToArray(), History = m.History.Select(RecordTransitionYaml.From).ToArray(),
        ReviewedAt = m.ReviewedAt?.ToString("O"), ReviewedSources = m.ReviewedSources?.Select(KnowledgeSourceRefYaml.From).ToArray(), RejectionReason = m.RejectionReason, RelatedRecords = m.RelatedRecords };
    public KnowledgeRecord ToRecord() => new(SchemaVersion, Id, Type, Kind, Status, Title, Project,
        ValidFrom is null ? null : DateTimeOffset.Parse(ValidFrom), ValidTo is null ? null : DateTimeOffset.Parse(ValidTo), DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt),
        Access, SourceRefs.Select(x => x.ToRef()).ToArray(), History.Select(x => x.ToTransition()).ToArray(), ReviewedAt is null ? null : DateTimeOffset.Parse(ReviewedAt), ReviewedSources?.Select(x => x.ToRef()).ToArray(), RejectionReason, RelatedRecords);
}

public sealed class KnowledgeRepository
{
    private static readonly ISerializer Serializer = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
    private readonly string root;
    private readonly Func<Guid, (SourceRecord Record, string Directory)> getSource;
    private readonly Func<string, IDisposable> lockFactory;
    public KnowledgeRepository(string root, Func<Guid, (SourceRecord Record, string Directory)> getSource, Func<string, IDisposable> lockFactory)
    {
        this.root = Path.Combine(root, "knowledge"); this.getSource = getSource; this.lockFactory = lockFactory;
    }
    private string Folder(string status) => Path.Combine(root, status switch { "draft" => "drafts", "reviewed" => "reviewed", "archived" => "archived", _ => throw new InvalidDataException($"Invalid record status: {status}") });
    private string JournalPath => Path.Combine(root, ".record-transition.json");
    public IReadOnlyList<KnowledgeRecordView> List(string? status = null)
    {
        RecoverTransition();
        var statuses = status is null ? new[] { "draft", "reviewed", "archived" } : new[] { status };
        return statuses.SelectMany(s => Directory.Exists(Folder(s)) ? Directory.EnumerateFiles(Folder(s), "*.md") : Array.Empty<string>())
            .Select(Read).OrderBy(x => x.Metadata.CreatedAt).ToArray();
    }
    public KnowledgeRecordView Show(Guid id)
    {
        var hits = List().Where(x => x.Metadata.Id == id).ToArray();
        return hits.Length == 1 ? hits[0] : throw new FileNotFoundException($"Knowledge record {id} was not found");
    }
    public IReadOnlyList<object> ShowSources(Guid id)
    {
        var record = Show(id);
        return record.Metadata.SourceRefs.Select(reference =>
        {
            var (_, dir) = getSource(reference.SourceId); var path = Path.Combine(dir, "extracted.txt");
            if (!File.Exists(path)) return (object)new { source_id = reference.SourceId, reference.Quote, context = (string?)null, stale = true };
            var lines = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var start = Math.Max(1, reference.StartLine - 3); var end = Math.Min(lines.Length, reference.EndLine + 3);
            var context = string.Join("\n", lines[(start - 1)..end].Select((line, offset) => $"{start + offset}: {line}"));
            var stale = Hash(string.Join("\n", lines)) != reference.TextSha256;
            return (object)new { source_id = reference.SourceId, reference.StartLine, reference.EndLine, reference.Quote, context, stale };
        }).ToArray();
    }
    private KnowledgeRecordView Read(string path)
    {
        var content = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!content.StartsWith("---\n", StringComparison.Ordinal)) throw new InvalidDataException($"{path}: YAML front matter is missing");
        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"{path}: YAML front matter is not closed");
        try
        {
            var metadata = Deserializer.Deserialize<KnowledgeRecordYaml>(content[4..end])?.ToRecord() ?? throw new InvalidDataException("empty front matter");
            if (metadata.SchemaVersion != "1" || metadata.Id == Guid.Empty || Path.GetFileNameWithoutExtension(path) != metadata.Id.ToString() || metadata.SourceRefs is null || metadata.History is null ||
                metadata.Type is not ("fact" or "decision" or "meeting" or "task" or "risk" or "question") || metadata.Kind is not ("source_claim" or "inference") ||
                metadata.Status is not ("draft" or "reviewed" or "archived"))
                throw new InvalidDataException("invalid knowledge record schema or identity");
            return new(metadata, content[(end + 5)..], path);
        }
        catch (Exception ex) { throw new InvalidDataException($"{path}: invalid YAML record: {ex.Message}", ex); }
    }
    private static string Render(KnowledgeRecord metadata, string body)
    {
        return "---\n" + Serializer.Serialize(KnowledgeRecordYaml.From(metadata)).TrimEnd() + "\n---\n" + body.TrimStart('\n') + (body.EndsWith('\n') ? "" : "\n");
    }
    private void Save(KnowledgeRecord metadata, string body)
    {
        Directory.CreateDirectory(Folder(metadata.Status)); var path = Path.Combine(Folder(metadata.Status), metadata.Id + ".md"); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, Render(metadata, body), new UTF8Encoding(false)); File.Move(temp, path, true);
    }
    private void Transition(KnowledgeRecord metadata, string body, string oldPath)
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(Folder(metadata.Status));
        var newPath = Path.Combine(Folder(metadata.Status), metadata.Id + ".md");
        if (string.Equals(oldPath, newPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { Save(metadata, body); return; }
        var journal = JsonSerializer.Serialize(new { old_path = oldPath, new_path = newPath }); var tempJournal = JournalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tempJournal, journal, new UTF8Encoding(false)); File.Move(tempJournal, JournalPath, true);
        try { Save(metadata, body); if (File.Exists(oldPath)) File.Delete(oldPath); File.Delete(JournalPath); }
        catch { RecoverTransition(); throw; }
    }
    private void RecoverTransition()
    {
        if (!File.Exists(JournalPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(JournalPath)); var oldPath = document.RootElement.GetProperty("old_path").GetString(); var newPath = document.RootElement.GetProperty("new_path").GetString();
            if (oldPath is not null && newPath is not null && File.Exists(newPath) && File.Exists(oldPath)) File.Delete(oldPath);
            File.Delete(JournalPath);
        }
        catch (Exception ex) { throw new InvalidDataException($"{JournalPath}: record transition recovery failed: {ex.Message}", ex); }
    }
    public KnowledgeRecordView Draft(Guid sourceId, string title, string body, string type = "fact", string kind = "source_claim")
    {
        using var operation = lockFactory("record draft"); var (source, dir) = getSource(sourceId); var textPath = Path.Combine(dir, "extracted.txt");
        if (!File.Exists(textPath)) throw new InvalidDataException($"Source {sourceId}: extracted text is unavailable");
        var text = File.ReadAllText(textPath, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal); var lines = text.Split('\n');
        var reference = new KnowledgeSourceRef(sourceId, Hash(text), 1, Math.Max(1, lines.Length), text);
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        var metadata = new KnowledgeRecord("1", id, type, kind, "draft", title, null, null, null, now, now,
            Access(new[] { source.Access }), new[] { reference }, new[] { new RecordTransition("draft", now) });
        Save(metadata, body); return Show(id);
    }
    public KnowledgeRecordView DraftCandidate(Guid sourceId, string title, string body, int startLine, int endLine, string quote, string type = "fact", string kind = "source_claim")
    {
        using var operation = lockFactory("record extract"); var (source, dir) = getSource(sourceId); var path = Path.Combine(dir, "extracted.txt");
        if (!File.Exists(path)) throw new InvalidDataException($"Source {sourceId}: extracted text is unavailable");
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal); var lines = text.Split('\n');
        if (startLine < 1 || endLine < startLine || endLine > lines.Length || string.Join("\n", lines[(startLine - 1)..endLine]) != quote) throw new InvalidDataException($"{sourceId}: candidate citation does not match extracted text");
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var reference = new KnowledgeSourceRef(sourceId, Hash(text), startLine, endLine, quote);
        var metadata = new KnowledgeRecord("1", id, type, kind, "draft", title, null, null, null, now, now, Access(new[] { source.Access }), new[] { reference }, new[] { new RecordTransition("draft", now) });
        Save(metadata, body); return Show(id);
    }
    public KnowledgeRecordView Edit(Guid id, string markdown)
    {
        using var operation = lockFactory("record edit"); var current = Show(id); var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) throw new InvalidDataException("Edited record must retain YAML front matter");
        var temp = Path.Combine(Path.GetTempPath(), "kb-edit-" + Guid.NewGuid().ToString("N") + ".md"); File.WriteAllText(temp, normalized);
        KnowledgeRecordView parsed; try { parsed = Read(temp); } finally { File.Delete(temp); }
        if (parsed.Metadata.Id != id) throw new InvalidDataException("Record ID cannot be changed");
        var metadata = parsed.Metadata with { Status = "draft", UpdatedAt = DateTimeOffset.UtcNow, ReviewedAt = null, ReviewedSources = null };
        if (current.Metadata.Status == "reviewed") metadata = metadata with { History = metadata.History.Append(new RecordTransition("draft", metadata.UpdatedAt, "edited_after_review")).ToArray() };
        Transition(metadata, parsed.Body, current.Path); return Show(id);
    }
    public KnowledgeRecordView Accept(Guid id, IReadOnlyCollection<Guid> confirmedSources)
    {
        using var operation = lockFactory("record accept"); var current = Show(id); var m = current.Metadata;
        var required = m.SourceRefs.Select(x => x.SourceId).Distinct().Order().ToArray();
        if (!required.SequenceEqual(confirmedSources.Distinct().Order())) throw new InvalidDataException("Pass --confirm-source once for every source referenced by this record");
        var access = new List<string>();
        foreach (var reference in m.SourceRefs)
        {
            var (source, dir) = getSource(reference.SourceId); var textPath = Path.Combine(dir, "extracted.txt");
            if (!File.Exists(textPath)) throw new InvalidDataException($"{reference.SourceId}: source_text_missing");
            var text = File.ReadAllText(textPath, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal); var lines = text.Split('\n');
            if (Hash(text) != reference.TextSha256) throw new InvalidDataException($"{reference.SourceId}: stale_source_hash");
            if (reference.StartLine < 1 || reference.EndLine < reference.StartLine || reference.EndLine > lines.Length) throw new InvalidDataException($"{reference.SourceId}: invalid_line_range");
            if (string.Join("\n", lines[(reference.StartLine - 1)..reference.EndLine]) != reference.Quote) throw new InvalidDataException($"{reference.SourceId}: quote_mismatch");
            access.Add(source.Access);
        }
        if (m.Kind == "source_claim" && m.SourceRefs.Length == 0) throw new InvalidDataException("Source claims require at least one citation");
        var now = DateTimeOffset.UtcNow; var updated = m with { Status = "reviewed", Access = Access(access), UpdatedAt = now, ReviewedAt = now,
            ReviewedSources = m.SourceRefs.ToArray(), History = m.History.Append(new RecordTransition("reviewed", now)).ToArray() };
        Transition(updated, current.Body, current.Path); return Show(id);
    }
    public KnowledgeRecordView Reject(Guid id, string reason)
    {
        using var operation = lockFactory("record reject"); if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Rejection reason is required", nameof(reason));
        var current = Show(id); var now = DateTimeOffset.UtcNow; var updated = current.Metadata with { Status = "archived", RejectionReason = reason, UpdatedAt = now,
            History = current.Metadata.History.Append(new RecordTransition("archived", now, reason)).ToArray() };
        Transition(updated, current.Body, current.Path); return Show(id);
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Access(IEnumerable<string> values)
    {
        var list = values.Select(x => x is "local_only" or "unclassified" or "external_allowed" ? x : "local_only").ToArray();
        if (list.Contains("local_only")) return "local_only"; if (list.Length == 0 || list.Contains("unclassified")) return "unclassified"; return "external_allowed";
    }
}
