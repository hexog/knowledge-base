using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KnowledgeBase.Core;

public sealed record KnowledgeSourceRef(Guid SourceId, string TextSha256, int StartLine, int EndLine, string Quote, string? QuoteSha256 = null);
public sealed record RecordTransition(string Status, DateTimeOffset At, string? Reason = null);
public sealed record RelatedRecord(string Type, Guid RecordId);
public sealed record KnowledgeRecord(string SchemaVersion, Guid Id, string Type, string Kind, string Status, string Title,
    string? Project, DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string Access, KnowledgeSourceRef[] SourceRefs, RecordTransition[] History, DateTimeOffset? ReviewedAt = null,
    KnowledgeSourceRef[]? ReviewedSources = null, string? RejectionReason = null, RelatedRecord[]? RelatedRecords = null,
    int? ReviewedSnapshotVersion = null, string? ReviewedContentSha256 = null, string? OperationId = null,
    string? AdapterVersion = null, string? ModelVersion = null, string? SourceTextSha256 = null,
    string? AccessOverride = null);
public sealed record KnowledgeRecordView(KnowledgeRecord Metadata, string Body, string Path, string? StaleReason = null, string? IndexStatus = null);

public sealed class KnowledgeSourceRefYaml
{
    public Guid SourceId { get; set; } public string TextSha256 { get; set; } = ""; public int StartLine { get; set; } public int EndLine { get; set; } public string Quote { get; set; } = ""; public string? QuoteSha256 { get; set; }
    public KnowledgeSourceRef ToRef() => new(SourceId, TextSha256, StartLine, EndLine, Quote, QuoteSha256);
    public static KnowledgeSourceRefYaml From(KnowledgeSourceRef x) => new() { SourceId = x.SourceId, TextSha256 = x.TextSha256, StartLine = x.StartLine, EndLine = x.EndLine, Quote = x.Quote, QuoteSha256 = x.QuoteSha256 };
}
public sealed class RecordTransitionYaml
{
    public string Status { get; set; } = ""; public string At { get; set; } = ""; public string? Reason { get; set; }
    public RecordTransition ToTransition() => new(Status, DateTimeOffset.Parse(At), Reason);
    public static RecordTransitionYaml From(RecordTransition x) => new() { Status = x.Status, At = x.At.ToString("O"), Reason = x.Reason };
}
public sealed class RelatedRecordYaml
{
    public string Type { get; set; } = ""; public Guid RecordId { get; set; }
    public RelatedRecord ToRelated() => new(Type, RecordId);
    public static RelatedRecordYaml From(RelatedRecord x) => new() { Type = x.Type, RecordId = x.RecordId };
}
public sealed class KnowledgeRecordYaml
{
    public string SchemaVersion { get; set; } = "1"; public Guid Id { get; set; } public string Type { get; set; } = "fact"; public string Kind { get; set; } = "source_claim";
    public string Status { get; set; } = "draft"; public string Title { get; set; } = ""; public string? Project { get; set; }
    public string? ValidFrom { get; set; } public string? ValidTo { get; set; } public string CreatedAt { get; set; } = ""; public string UpdatedAt { get; set; } = "";
    public string Access { get; set; } = "unclassified"; public KnowledgeSourceRefYaml[] SourceRefs { get; set; } = Array.Empty<KnowledgeSourceRefYaml>();
    public RecordTransitionYaml[] History { get; set; } = Array.Empty<RecordTransitionYaml>(); public string? ReviewedAt { get; set; }
    public KnowledgeSourceRefYaml[]? ReviewedSources { get; set; } public string? RejectionReason { get; set; } public RelatedRecordYaml[]? RelatedRecords { get; set; }
    public int? ReviewedSnapshotVersion { get; set; } public string? ReviewedContentSha256 { get; set; }
    public string? OperationId { get; set; } public string? AdapterVersion { get; set; } public string? ModelVersion { get; set; } public string? SourceTextSha256 { get; set; }
    public string? AccessOverride { get; set; }
    public static KnowledgeRecordYaml From(KnowledgeRecord m) => new() { SchemaVersion = m.SchemaVersion, Id = m.Id, Type = m.Type, Kind = m.Kind,
        Status = m.Status, Title = m.Title, Project = m.Project, ValidFrom = m.ValidFrom?.ToString("O"), ValidTo = m.ValidTo?.ToString("O"),
        CreatedAt = m.CreatedAt.ToString("O"), UpdatedAt = m.UpdatedAt.ToString("O"), Access = m.Access, SourceRefs = m.SourceRefs.Select(KnowledgeSourceRefYaml.From).ToArray(), History = m.History.Select(RecordTransitionYaml.From).ToArray(),
        ReviewedAt = m.ReviewedAt?.ToString("O"), ReviewedSources = m.ReviewedSources?.Select(KnowledgeSourceRefYaml.From).ToArray(), RejectionReason = m.RejectionReason,
        RelatedRecords = m.RelatedRecords?.Select(RelatedRecordYaml.From).ToArray(), ReviewedSnapshotVersion = m.ReviewedSnapshotVersion, ReviewedContentSha256 = m.ReviewedContentSha256,
        OperationId = m.OperationId, AdapterVersion = m.AdapterVersion, ModelVersion = m.ModelVersion, SourceTextSha256 = m.SourceTextSha256, AccessOverride = m.AccessOverride };
    public KnowledgeRecord ToRecord() => new(SchemaVersion, Id, Type, Kind, Status, Title, Project,
        ValidFrom is null ? null : DateTimeOffset.Parse(ValidFrom), ValidTo is null ? null : DateTimeOffset.Parse(ValidTo), DateTimeOffset.Parse(CreatedAt), DateTimeOffset.Parse(UpdatedAt),
        Access, SourceRefs.Select(x => x.ToRef()).ToArray(), History.Select(x => x.ToTransition()).ToArray(), ReviewedAt is null ? null : DateTimeOffset.Parse(ReviewedAt),
        ReviewedSources?.Select(x => x.ToRef()).ToArray(), RejectionReason, RelatedRecords?.Select(x => x.ToRelated()).ToArray(), ReviewedSnapshotVersion, ReviewedContentSha256,
        OperationId, AdapterVersion, ModelVersion, SourceTextSha256, AccessOverride);
}

public sealed class KnowledgeRepository
{
    private const int SnapshotVersion = 1;
    private static readonly ISerializer Serializer = new SerializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance).Build();
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
        var statuses = status is null ? new[] { "draft", "reviewed", "archived" } : status == "stale" ? new[] { "reviewed" } : new[] { status };
        var records = statuses.SelectMany(s => Directory.Exists(Folder(s)) ? Directory.EnumerateFiles(Folder(s), "*.md") : Array.Empty<string>())
            .Select(Read).Select(Effective).OrderBy(x => x.Metadata.CreatedAt).ToArray();
        if (status == "stale") return records.Where(x => x.StaleReason is not null).ToArray();
        return status is null ? records : records.Where(x => x.Metadata.Status == status).ToArray();
    }
    public IReadOnlyList<KnowledgeRecordView> ListCanonical()
    {
        var statuses = new[] { "draft", "reviewed", "archived" };
        return statuses.SelectMany(s => Directory.Exists(Folder(s)) ? Directory.EnumerateFiles(Folder(s), "*.md") : Array.Empty<string>())
            .Select(Read).OrderBy(x => x.Metadata.CreatedAt).ToArray();
    }
    public int MigrateToSchema3()
    {
        var changed = 0;
        foreach (var view in ListCanonical())
        {
            if (view.Metadata.SchemaVersion == "3") continue;
            var metadata = view.Metadata with { SchemaVersion = "3", AccessOverride = view.Metadata.Access };
            if (metadata.Status == "reviewed" && metadata.ReviewedSnapshotVersion == SnapshotVersion)
                metadata = metadata with { ReviewedContentSha256 = ContentHash(metadata, view.Body) };
            Save(metadata, view.Body); changed++;
        }
        return changed;
    }
    public KnowledgeRecordView SetAccessOverride(Guid id, string? level)
    {
        if (level is not null and not ("local_only" or "unclassified" or "external_allowed")) throw new ArgumentException("Unsupported access level", nameof(level));
        RecoverTransition();
        var view = Show(id); var updated = view.Metadata with { SchemaVersion = "3", AccessOverride = level, UpdatedAt = DateTimeOffset.UtcNow };
        Save(updated, view.Body); return Show(id);
    }
    public void RecoverPendingTransitions() => RecoverTransition();
    public IReadOnlyList<string> VerifyHistory()
    {
        var errors = new List<string>(); var history = Path.Combine(root, "history");
        if (!Directory.Exists(history)) return errors;
        foreach (var snapshot in Directory.EnumerateFiles(history, "*.md", SearchOption.AllDirectories))
        {
            var metadata = snapshot + ".json";
            try
            {
                if (!File.Exists(metadata)) { errors.Add($"{Path.GetRelativePath(root, snapshot)}: history_metadata_missing"); continue; }
                using var json = JsonDocument.Parse(File.ReadAllText(metadata));
                if (json.RootElement.GetProperty("format_version").GetInt32() != 1 || json.RootElement.GetProperty("sha256").GetString() != FileHash(File.ReadAllText(snapshot, Encoding.UTF8))) errors.Add($"{Path.GetRelativePath(root, snapshot)}: history_hash_mismatch");
            }
            catch (Exception ex) { errors.Add($"{Path.GetRelativePath(root, snapshot)}: history_invalid ({ex.Message})"); }
        }
        return errors;
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
            var lines = Normalize(File.ReadAllText(path, Encoding.UTF8)).Split('\n');
            var start = Math.Max(1, reference.StartLine - 3); var end = Math.Min(lines.Length, reference.EndLine + 3);
            var context = string.Join("\n", lines[(start - 1)..end].Select((line, offset) => $"{start + offset}: {line}"));
            var stale = Hash(string.Join("\n", lines)) != reference.TextSha256 || !CitationMatches(lines, reference) || reference.QuoteSha256 is not null && reference.QuoteSha256 != Hash(reference.Quote);
            return (object)new { source_id = reference.SourceId, reference.StartLine, reference.EndLine, reference.Quote, context, stale };
        }).ToArray();
    }
    public KnowledgeRecordView Read(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"{path}: canonical records cannot be symbolic links");
        var content = Normalize(File.ReadAllText(path, Encoding.UTF8));
        var view = Parse(content, path);
        if (!string.Equals(Path.GetFileNameWithoutExtension(path), view.Metadata.Id.ToString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{path}: record ID does not match canonical filename");
        var expectedFolder = Folder(view.Metadata.Status);
        if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(expectedFolder), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException($"{path}: record status does not match canonical folder '{view.Metadata.Status}'");
        return view;
    }
    private static KnowledgeRecordView Parse(string content, string path)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal)) throw new InvalidDataException($"{path}: YAML front matter is missing");
        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"{path}: YAML front matter is not closed");
        try
        {
            var metadata = Deserializer.Deserialize<KnowledgeRecordYaml>(content[4..end])?.ToRecord() ?? throw new InvalidDataException("empty front matter");
            Validate(metadata, content[(end + 5)..]);
            return new(metadata, content[(end + 5)..], path);
        }
        catch (Exception ex) when (ex is not InvalidDataException || ex.Message != $"{path}: invalid YAML record: {ex.InnerException?.Message}")
        { throw new InvalidDataException($"{path}: invalid YAML record: {ex.Message}", ex); }
    }
    private static void Validate(KnowledgeRecord m, string body)
    {
        if (m.SchemaVersion is not ("1" or "2" or "3") || m.Id == Guid.Empty || m.SourceRefs is null || m.History is null || m.SourceRefs.Any(x => x is null) || m.History.Any(x => x is null)) throw new InvalidDataException("invalid schema version or missing required fields");
        if (m.Type is not ("fact" or "decision" or "meeting" or "task" or "risk" or "question")) throw new InvalidDataException($"unsupported type '{m.Type}'");
        if (m.Kind is not ("source_claim" or "inference")) throw new InvalidDataException($"unsupported kind '{m.Kind}'");
        if (m.Status is not ("draft" or "reviewed" or "archived")) throw new InvalidDataException($"unsupported status '{m.Status}'");
        if (string.IsNullOrWhiteSpace(m.Title)) throw new InvalidDataException("title is required");
        if (string.IsNullOrWhiteSpace(body)) throw new InvalidDataException("body is required");
        if (m.Access is not ("local_only" or "unclassified" or "external_allowed")) throw new InvalidDataException($"unsupported access '{m.Access}'");
        if (m.AccessOverride is not null and not ("local_only" or "unclassified" or "external_allowed")) throw new InvalidDataException("unsupported access_override");
        foreach (var r in m.SourceRefs) ValidateReference(r);
        var related = m.RelatedRecords ?? Array.Empty<RelatedRecord>();
        if (related.Any(x => x.RecordId == Guid.Empty || x.RecordId == m.Id || x.Type is not ("conflicts_with" or "supersedes"))) throw new InvalidDataException("related_records contains an invalid type, UUID, or self-reference");
        if (related.Select(x => (x.Type, x.RecordId)).Distinct().Count() != related.Length) throw new InvalidDataException("related_records contains duplicates");
    }
    private static void ValidateReference(KnowledgeSourceRef r)
    {
        if (r.SourceId == Guid.Empty || r.StartLine < 1 || r.EndLine < r.StartLine || string.IsNullOrWhiteSpace(r.Quote) || !IsSha256(r.TextSha256) || (r.QuoteSha256 is not null && (!IsSha256(r.QuoteSha256) || Hash(r.Quote) != r.QuoteSha256))) throw new InvalidDataException("source_refs contains an invalid source ID, range, quote, or text hash");
    }
    private static string Render(KnowledgeRecord metadata, string body) => "---\n" + Serializer.Serialize(KnowledgeRecordYaml.From(metadata)).TrimEnd() + "\n---\n" + body.TrimStart('\n') + (body.EndsWith('\n') ? "" : "\n");
    public static string ContentHash(KnowledgeRecord m, string body)
    {
        var canonical = JsonSerializer.Serialize(new { version = 1, m.SchemaVersion, m.Id, m.Type, m.Kind, m.Title, m.Project, m.ValidFrom, m.ValidTo, m.Access,
            source_refs = m.SourceRefs.Select(x => new { x.SourceId, x.TextSha256, x.StartLine, x.EndLine, x.Quote }).ToArray(),
            related_records = (m.RelatedRecords ?? Array.Empty<RelatedRecord>()).Select(x => new { x.Type, x.RecordId }).ToArray(), body = Normalize(body) });
        return Hash(canonical);
    }
    public static string FileHash(string markdown) => Hash(Normalize(markdown));
    private void Save(KnowledgeRecord metadata, string body)
    {
        Validate(metadata, body); var path = Path.Combine(Folder(metadata.Status), metadata.Id + ".md"); _ = SafeJournalPath(Path.GetRelativePath(root, path)); Directory.CreateDirectory(Folder(metadata.Status)); var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, Render(metadata, body), new UTF8Encoding(false)); File.Move(temp, path, true);
    }
    private void SaveHistory(KnowledgeRecordView current, string reason)
    {
        var folder = Path.Combine(root, "history", current.Metadata.Id.ToString()); _ = SafeJournalPath(Path.GetRelativePath(root, folder)); Directory.CreateDirectory(folder);
        var content = File.ReadAllText(current.Path, Encoding.UTF8); var hash = FileHash(content); var target = Path.Combine(folder, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{hash[..12]}.md");
        if (!File.Exists(target))
        {
            var temp = target + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllText(temp, content, new UTF8Encoding(false)); File.Move(temp, target);
            File.WriteAllText(target + ".json", JsonSerializer.Serialize(new { format_version = 1, sha256 = hash, reason }), new UTF8Encoding(false));
        }
    }
    private void Transition(KnowledgeRecord metadata, string body, string oldPath)
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(Folder(metadata.Status)); var newPath = Path.Combine(Folder(metadata.Status), metadata.Id + ".md");
        if (SamePath(oldPath, newPath)) { Save(metadata, body); return; }
        string Rel(string p) { var rel = Path.GetRelativePath(root, p); if (Path.IsPathRooted(rel) || rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..")) throw new InvalidDataException("Record transition path escapes knowledge root"); return rel; }
        var oldRel = Rel(oldPath); var newRel = Rel(newPath); var journal = JsonSerializer.Serialize(new { format_version = 1, id = metadata.Id, old_path = oldRel, new_path = newRel, phase = "prepared", expected_sha256 = FileHash(Render(metadata, body)) });
        _ = SafeJournalPath(Path.GetRelativePath(root, JournalPath));
        WriteAtomic(JournalPath, journal);
        try { Save(metadata, body); WriteAtomic(JournalPath, journal.Replace("prepared", "saved", StringComparison.Ordinal)); if (File.Exists(oldPath)) File.Delete(oldPath); File.Delete(JournalPath); }
        catch { RecoverTransition(); throw; }
    }
    private void RecoverTransition()
    {
        _ = SafeJournalPath(Path.GetRelativePath(root, JournalPath));
        if (!File.Exists(JournalPath)) return;
        if (!File.Exists(JournalPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(JournalPath)); var rootElement = doc.RootElement;
            if (rootElement.GetProperty("format_version").GetInt32() != 1) throw new InvalidDataException("unsupported transition journal version");
            var oldPath = SafeJournalPath(rootElement.GetProperty("old_path").GetString()!); var newPath = SafeJournalPath(rootElement.GetProperty("new_path").GetString()!);
            var expectedHash = rootElement.GetProperty("expected_sha256").GetString();
            if (File.Exists(newPath))
            {
                if (FileHash(File.ReadAllText(newPath)) != expectedHash) throw new InvalidDataException("new record does not match the journal hash");
                if (File.Exists(oldPath)) File.Delete(oldPath); File.Delete(JournalPath); return;
            }
            if (File.Exists(oldPath)) { File.Delete(JournalPath); return; }
            throw new InvalidDataException("neither transition version matches the journal; restore a record from knowledge/history or backup");
        }
        catch (Exception ex) { throw new InvalidDataException($"{JournalPath}: transition recovery stopped: {ex.Message}", ex); }
    }
    private string SafeJournalPath(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("transition journal contains an absolute path");
        var path = Path.GetFullPath(Path.Combine(root, relative)); var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("transition journal path escapes knowledge root");
        for (var current = path; current.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("transition journal path traverses a symbolic link");
        return path;
    }
    private KnowledgeRecordView Effective(KnowledgeRecordView view)
    {
        if (view.Metadata.Status != "reviewed") return view;
        var reason = "review_snapshot_missing";
        if (view.Metadata.ReviewedSnapshotVersion != SnapshotVersion || !IsSha256(view.Metadata.ReviewedContentSha256)) return view with { Metadata = view.Metadata with { Status = "stale" }, StaleReason = reason };
        if (ContentHash(view.Metadata, view.Body) != view.Metadata.ReviewedContentSha256) reason = "record_content_changed";
        else if (view.Metadata.ReviewedSources is null || view.Metadata.ReviewedSources.Length == 0 || !SameReviewedSources(view.Metadata.ReviewedSources, view.Metadata.SourceRefs)) reason = "reviewed_sources_mismatch";
        else
        {
            reason = "source_changed_or_quote_mismatch";
            var allSourcesValid = true;
            foreach (var reference in view.Metadata.ReviewedSources)
            {
                try
                {
                    var (_, dir) = getSource(reference.SourceId); var path = Path.Combine(dir, "extracted.txt");
                    if (!File.Exists(path)) { allSourcesValid = false; break; }
                    var text = Normalize(File.ReadAllText(path, Encoding.UTF8)); var lines = text.Split('\n');
                    if (Hash(text) != reference.TextSha256 || !CitationMatches(lines, reference) || reference.QuoteSha256 != Hash(reference.Quote)) { allSourcesValid = false; break; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { reason = "source_missing"; allSourcesValid = false; break; }
            }
            if (allSourcesValid) reason = "";
        }
        return reason.Length == 0 ? view : view with { Metadata = view.Metadata with { Status = "stale" }, StaleReason = reason };
    }
    public KnowledgeRecordView Draft(Guid sourceId, string title, string body, string type = "fact", string kind = "source_claim") => throw new InvalidDataException("Manual drafts require an explicit citation; use DraftCandidate with line range and quote");
    public KnowledgeRecordView DraftCandidate(Guid sourceId, string title, string body, int startLine, int endLine, string quote, string type = "fact", string kind = "source_claim", string? operationId = null, string? adapterVersion = null, string? modelVersion = null, Action<KnowledgeRecordView>? index = null)
    {
        using var operation = lockFactory("record draft"); RecoverTransition(); var (source, dir) = getSource(sourceId); var path = Path.Combine(dir, "extracted.txt");
        if (!File.Exists(path)) throw new InvalidDataException($"Source {sourceId}: extracted text is unavailable");
        var text = Normalize(File.ReadAllText(path, Encoding.UTF8)); var lines = text.Split('\n'); var reference = new KnowledgeSourceRef(sourceId, Hash(text), startLine, endLine, quote, Hash(quote));
        if (!CitationMatches(lines, reference)) throw new InvalidDataException($"{sourceId}: candidate citation does not match extracted text");
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var metadata = new KnowledgeRecord("3", id, type, kind, "draft", title, null, null, null, now, now,
            "unclassified", new[] { reference }, new[] { new RecordTransition("draft", now) }, OperationId: operationId, AdapterVersion: adapterVersion, ModelVersion: modelVersion, SourceTextSha256: Hash(text));
        Save(metadata, body); return IndexOrMark(Show(id), index);
    }
    public KnowledgeRecordView DraftAgent(string title, string body, IReadOnlyList<(Guid SourceId, int StartLine, int EndLine, string Quote)> citations, Action<KnowledgeRecordView>? index = null)
    {
        RecoverTransition();
        if (citations.Count is < 1 or > 10) throw new InvalidDataException("A draft must cite between 1 and 10 sources");
        var references = new List<KnowledgeSourceRef>();
        foreach (var citation in citations)
        {
            var (_, dir) = getSource(citation.SourceId); var path = Path.Combine(dir, "extracted.txt");
            if (!File.Exists(path)) throw new InvalidDataException("Source text is unavailable");
            var text = Normalize(File.ReadAllText(path, Encoding.UTF8)); var reference = new KnowledgeSourceRef(citation.SourceId, Hash(text), citation.StartLine, citation.EndLine, citation.Quote, Hash(citation.Quote));
            if (!CitationMatches(text.Split('\n'), reference)) throw new InvalidDataException("Draft citation does not match source text");
            references.Add(reference);
        }
        if (references.Select(x => x.SourceId).Distinct().Count() != references.Count) throw new InvalidDataException("Duplicate source citations are not allowed");
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        var metadata = new KnowledgeRecord("3", id, "fact", "source_claim", "draft", title, null, null, null, now, now,
            "unclassified", references.ToArray(), new[] { new RecordTransition("draft", now) });
        Save(metadata, body); return IndexOrMark(Show(id), index);
    }
    public KnowledgeRecordView Edit(Guid id, string markdown, string? expectedFileHash = null, Action<KnowledgeRecordView>? index = null)
    {
        using var operation = lockFactory("record edit"); RecoverTransition(); var located = Show(id); var current = Read(located.Path); var currentMarkdown = File.ReadAllText(current.Path, Encoding.UTF8);
        if (expectedFileHash is not null && FileHash(currentMarkdown) != expectedFileHash) throw new IOException($"Edit conflict for {id}: the record changed while the editor was open. Your edited copy remains available at the supplied temporary path.");
        var parsed = Parse(Normalize(markdown), "editor input"); if (parsed.Metadata.Id != id) throw new InvalidDataException("Record ID cannot be changed");
        var proposed = parsed.Metadata;
        var changed = ContentHash(current.Metadata, current.Body) != ContentHash(proposed, parsed.Body);
        if (changed) SaveHistory(current, "edited");
        var now = DateTimeOffset.UtcNow;
        var metadata = proposed with { Status = current.Metadata.Status == "archived" ? "archived" : changed ? "draft" : current.Metadata.Status,
            UpdatedAt = changed ? now : current.Metadata.UpdatedAt,
            ReviewedSources = changed ? null : current.Metadata.ReviewedSources, ReviewedSnapshotVersion = changed ? null : current.Metadata.ReviewedSnapshotVersion,
            ReviewedContentSha256 = changed ? null : current.Metadata.ReviewedContentSha256,
            ReviewedAt = changed ? null : current.Metadata.ReviewedAt,
            History = changed ? current.Metadata.History.Append(new RecordTransition("draft", now, "edited")).ToArray() : current.Metadata.History };
        Transition(metadata, parsed.Body, current.Path); return IndexOrMark(Show(id), index);
    }
    public KnowledgeRecordView Accept(Guid id, IReadOnlyCollection<Guid> confirmedSources, Action<KnowledgeRecordView>? index = null)
    {
        using var operation = lockFactory("record accept"); RecoverTransition(); var located = Show(id); var current = Read(located.Path); var m = current.Metadata;
        if (m.Kind is not ("source_claim" or "inference") || m.SourceRefs.Length == 0) throw new InvalidDataException($"{m.Kind} records require at least one citation");
        var required = m.SourceRefs.Select(x => x.SourceId).Distinct().Order().ToArray();
        if (!required.SequenceEqual(confirmedSources.Distinct().Order())) throw new InvalidDataException("Pass --confirm-source once for every source referenced by this record");
        var access = new List<string>();
        foreach (var reference in m.SourceRefs)
        {
            var (source, dir) = getSource(reference.SourceId); var textPath = Path.Combine(dir, "extracted.txt");
            if (!File.Exists(textPath)) throw new InvalidDataException($"{reference.SourceId}: source_text_missing");
            var text = Normalize(File.ReadAllText(textPath, Encoding.UTF8)); var lines = text.Split('\n');
            if (Hash(text) != reference.TextSha256) throw new InvalidDataException($"{reference.SourceId}: stale_source_hash");
            if (!CitationMatches(lines, reference)) throw new InvalidDataException($"{reference.SourceId}: quote_mismatch_or_invalid_line_range");
            access.Add(source.Access);
        }
        ValidateRelatedTargets(m);
        SaveHistory(current, "accepted");
        var now = DateTimeOffset.UtcNow; var updated = m with { Status = "reviewed", Access = Access(access), UpdatedAt = now, ReviewedAt = now,
            ReviewedSources = m.SourceRefs.Select(x => x with { QuoteSha256 = Hash(x.Quote) }).ToArray(), ReviewedSnapshotVersion = SnapshotVersion,
            History = m.History.Append(new RecordTransition("reviewed", now)).ToArray() };
        updated = updated with { ReviewedContentSha256 = ContentHash(updated, current.Body) };
        Transition(updated, current.Body, current.Path); return IndexOrMark(Show(id), index);
    }
    private void ValidateRelatedTargets(KnowledgeRecord m)
    {
        foreach (var relation in m.RelatedRecords ?? Array.Empty<RelatedRecord>())
            if (!List().Any(x => x.Metadata.Id == relation.RecordId)) throw new InvalidDataException($"related_records target {relation.RecordId} does not exist");
    }
    public KnowledgeRecordView Reject(Guid id, string reason, Action<KnowledgeRecordView>? index = null)
    {
        using var operation = lockFactory("record reject"); RecoverTransition(); if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Rejection reason is required", nameof(reason));
        var located = Show(id); var current = Read(located.Path); SaveHistory(current, "archived: " + reason); var now = DateTimeOffset.UtcNow; var updated = current.Metadata with { Status = "archived", RejectionReason = reason, UpdatedAt = now,
            History = current.Metadata.History.Append(new RecordTransition("archived", now, reason)).ToArray() };
        Transition(updated, current.Body, current.Path); return IndexOrMark(Show(id), index);
    }
    private static KnowledgeRecordView IndexOrMark(KnowledgeRecordView record, Action<KnowledgeRecordView>? index)
    {
        if (index is null) return record;
        try { index(record); return record; }
        catch (Exception ex) { return record with { IndexStatus = $"saved_index_stale: {ex.Message}" }; }
    }
    private static bool CitationMatches(string[] lines, KnowledgeSourceRef reference) => reference.StartLine >= 1 && reference.EndLine >= reference.StartLine && reference.EndLine <= lines.Length && !string.IsNullOrWhiteSpace(reference.Quote) && string.Join("\n", lines[(reference.StartLine - 1)..reference.EndLine]) == reference.Quote;
    private static bool SameReviewedSources(IReadOnlyList<KnowledgeSourceRef> reviewed, IReadOnlyList<KnowledgeSourceRef> current) => reviewed.Count == current.Count && reviewed.Zip(current).All(pair => (pair.First with { QuoteSha256 = null }) == (pair.Second with { QuoteSha256 = null }) && pair.First.QuoteSha256 == Hash(pair.First.Quote));
    private static bool SamePath(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static void WriteAtomic(string path, string content) { var temp = path + ".tmp-" + Guid.NewGuid().ToString("N"); File.WriteAllText(temp, content, new UTF8Encoding(false)); File.Move(temp, path, true); }
    private static string Access(IEnumerable<string> values)
    {
        var list = values.Select(x => x is "local_only" or "unclassified" or "external_allowed" ? x : "local_only").ToArray();
        if (list.Contains("local_only")) return "local_only"; if (list.Length == 0 || list.Contains("unclassified")) return "unclassified"; return "external_allowed";
    }
}
