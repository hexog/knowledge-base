using System.Text;
using System.Text.Json;

namespace KnowledgeBase.Core;

public sealed record FeedbackStatusEntry(string Status, DateTimeOffset At, string? Reason);

public sealed record FeedbackRecord(
    int SchemaVersion,
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Workflow,
    string Rating,
    string Category,
    string Status,
    string Note,
    string? Expected,
    Guid[] RecordRefs,
    Guid[] SourceRefs,
    string[] GatewayRequestIds,
    string? Client,
    string? Model,
    string? Resolution,
    FeedbackStatusEntry[] History);

public sealed record FeedbackAddResult(FeedbackRecord Feedback, Guid[] MissingRecords, Guid[] MissingSources);

public sealed class FeedbackRepository
{
    public const int MaxTextBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly HashSet<string> Workflows = new(StringComparer.Ordinal) { "prepare_meeting", "assess_decision", "question", "other" };
    private static readonly HashSet<string> Ratings = new(StringComparer.Ordinal) { "useful", "partly_useful", "not_useful" };
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal) { "positive", "wrong_fact", "incomplete_answer", "wrong_citation", "stale_knowledge", "retrieval_miss", "access_issue", "technical", "other" };
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "new", "triaged", "planned", "resolved", "dismissed" };
    private readonly string folder;
    private readonly Func<IDisposable> acquireLock;
    private readonly Func<Guid, bool> recordExists;
    private readonly Func<Guid, bool> sourceExists;
    private readonly Action<string, string> replaceFile;

    public FeedbackRepository(string root, Func<IDisposable> acquireLock, Func<Guid, bool>? recordExists = null, Func<Guid, bool>? sourceExists = null)
        : this(root, acquireLock, recordExists, sourceExists, (temporaryPath, targetPath) => File.Move(temporaryPath, targetPath, true))
    {
    }

    internal FeedbackRepository(string root, Func<IDisposable> acquireLock, Func<Guid, bool>? recordExists, Func<Guid, bool>? sourceExists, Action<string, string> replaceFile)
    {
        folder = Path.Combine(Path.GetFullPath(root), "feedback");
        this.acquireLock = acquireLock;
        this.recordExists = recordExists ?? (_ => false);
        this.sourceExists = sourceExists ?? (_ => false);
        this.replaceFile = replaceFile;
    }

    public FeedbackAddResult Add(string workflow, string rating, string category, string note, string? expected = null,
        Guid? record = null, Guid? source = null, string? requestId = null, string? client = null, string? model = null)
    {
        ValidateEnum(workflow, Workflows, "workflow"); ValidateEnum(rating, Ratings, "rating"); ValidateEnum(category, Categories, "category");
        ValidateText(note, "note", required: true); if (expected is not null) ValidateText(expected, "expected", required: false);
        using var operation = acquireLock(); Directory.CreateDirectory(folder);
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var value = new FeedbackRecord(1, id, now, now, workflow, rating, category, "new", note, expected,
            record is null ? Array.Empty<Guid>() : new[] { record.Value }, source is null ? Array.Empty<Guid>() : new[] { source.Value },
            string.IsNullOrWhiteSpace(requestId) ? Array.Empty<string>() : new[] { requestId }, client, model, null,
            new[] { new FeedbackStatusEntry("new", now, null) });
        Write(value);
        return new FeedbackAddResult(value,
            record is Guid recordId && !recordExists(recordId) ? new[] { recordId } : Array.Empty<Guid>(),
            source is Guid sourceId && !sourceExists(sourceId) ? new[] { sourceId } : Array.Empty<Guid>());
    }

    public IReadOnlyList<FeedbackRecord> List(string? status = null)
    {
        if (status is not null) ValidateEnum(status, Statuses, "status");
        if (!Directory.Exists(folder)) return Array.Empty<FeedbackRecord>();
        var values = Directory.EnumerateFiles(folder, "*.json").Select(ReadPath).Where(x => status is null || x.Status == status)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToArray();
        return values;
    }

    public FeedbackRecord Show(Guid id)
    {
        var path = FilePath(id);
        if (!File.Exists(path)) throw new FileNotFoundException($"Feedback {id} was not found", path);
        return ReadPath(path);
    }

    public FeedbackRecord SetStatus(Guid id, string status, string reason)
    {
        ValidateEnum(status, Statuses, "status");
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Status reason is required", nameof(reason));
        using var operation = acquireLock();
        var path = FilePath(id); var current = ReadPath(path);
        if (current.Status == status) return current;
        var now = DateTimeOffset.UtcNow;
        var updated = current with { Status = status, UpdatedAt = now, Resolution = status == "resolved" ? reason : null,
            History = current.History.Append(new FeedbackStatusEntry(status, now, reason)).ToArray() };
        Write(updated); return updated;
    }

    private string FilePath(Guid id) => Path.Combine(folder, id.ToString("D") + ".json");

    private FeedbackRecord ReadPath(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<FeedbackRecord>(File.ReadAllText(path, new UTF8Encoding(false, true)), Json)
                ?? throw new InvalidDataException("JSON is empty");
            if (value.SchemaVersion != 1 || value.Id == Guid.Empty || !string.Equals(Path.GetFileName(path), value.Id + ".json", StringComparison.OrdinalIgnoreCase) ||
                value.CreatedAt == default || value.UpdatedAt == default || value.RecordRefs is null || value.SourceRefs is null || value.GatewayRequestIds is null || value.History is null)
                throw new InvalidDataException("Unsupported schema or invalid identity fields");
            ValidateEnum(value.Workflow, Workflows, "workflow"); ValidateEnum(value.Rating, Ratings, "rating"); ValidateEnum(value.Category, Categories, "category"); ValidateEnum(value.Status, Statuses, "status");
            ValidateText(value.Note, "note", required: true); if (value.Expected is not null) ValidateText(value.Expected, "expected", required: false);
            if (value.History.Length == 0 || value.History.Any(x => x is null || !Statuses.Contains(x.Status) || x.At == default)) throw new InvalidDataException("Invalid status history");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException($"Invalid feedback file '{path}': {ex.Message}", ex); }
    }

    private void Write(FeedbackRecord value)
    {
        Directory.CreateDirectory(folder);
        var path = FilePath(value.Id); var temp = Path.Combine(folder, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            replaceFile(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void ValidateEnum(string value, HashSet<string> values, string name)
    {
        if (!values.Contains(value)) throw new ArgumentException($"Invalid {name} '{value}'. Allowed values: {string.Join(", ", values.Order())}", name);
    }

    private static void ValidateText(string value, string name, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} must not be empty", name);
        if (Encoding.UTF8.GetByteCount(value) > MaxTextBytes) throw new ArgumentException($"{name} exceeds {MaxTextBytes} UTF-8 bytes", name);
    }
}
