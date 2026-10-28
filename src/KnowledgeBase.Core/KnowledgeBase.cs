using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;

namespace KnowledgeBase.Core;

public sealed record ExtractorInfo(string Name, string Version, string Status, string? Error = null);
public sealed record SourceRecord(string SchemaVersion, Guid SourceId, string ContentSha256, string OriginalName,
    string SourceType, string? MimeType, DateTimeOffset ImportedAt, DateTimeOffset LastSeenAt, string? OriginPath,
    ExtractorInfo Extractor, string Access, string[] Aliases, string? OriginalRelativePath = null, string? ExtractedTextSha256 = null);
public sealed record ImportItem(string Path, string Status, Guid? SourceId = null, string? Message = null, string? Details = null);
public sealed record RebuildResult(int Indexed, int Skipped, int KnowledgeIndexed = 0);
public sealed record KnowledgeSearchResult(Guid Id, string Title, string Type, string Status, string Access, string Snippet, Guid[] SourceIds);
public sealed record ImportSummary(int Imported, int Duplicates, int Indexed, int Unsupported, int Errors, int Oversized,
    int SkippedSymlinks, IReadOnlyList<ImportItem> Items);
public sealed record SearchResult(Guid SourceId, string OriginalName, string SourceType, DateTimeOffset ImportedAt,
    string SourcePath, string Snippet, string Status);
public sealed record ExtractionResult(string? Text, string Name, string Status, string? Error = null);

public interface ITextExtractor { bool Supports(string path); ExtractionResult Extract(string path); }

public sealed class Utf8TextExtractor : ITextExtractor
{
    public bool Supports(string path) => !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public ExtractionResult Extract(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length == 0) return new("", "utf8", "empty_text");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            var text = reader.ReadToEnd();
            if (text.Contains('\0')) return new(null, "none", "unsupported");
            return string.IsNullOrWhiteSpace(text) ? new("", "utf8", "empty_text") : new(text, "utf8", "indexed");
        }
        catch (DecoderFallbackException) { return new(null, "none", "unsupported"); }
        catch (Exception ex) { return new(null, "utf8", "extraction_error", ex.Message); }
    }
}

public sealed class PdfTextExtractor : ITextExtractor
{
    public bool Supports(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public ExtractionResult Extract(string path)
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            var text = string.Join("\n\n", pdf.GetPages().Select(p => p.Text)).Trim();
            return string.IsNullOrWhiteSpace(text) ? new("", "pdfpig", "empty_text") : new(text, "pdfpig", "indexed");
        }
        catch (Exception ex) { return new(null, "pdfpig", "extraction_error", ex.Message); }
    }
}

public sealed class SourceRepository
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string sources;
    public SourceRepository(string root) => sources = Path.Combine(root, "sources");
    public string SourcesPath => sources;
    public string SourcePath(Guid id) => Path.Combine(sources, id.ToString());
    public IEnumerable<(SourceRecord Record, string Directory)> ReadSources(List<string>? problems = null)
    {
        if (!Directory.Exists(sources)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(sources).Where(d => !Path.GetFileName(d).StartsWith(".tmp-", StringComparison.Ordinal)))
        {
            var path = Path.Combine(dir, "source.json"); SourceRecord? record = null;
            try
            {
                if (!File.Exists(path)) throw new InvalidDataException("source.json is missing");
                record = JsonSerializer.Deserialize<SourceRecord>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException("source.json is empty");
                if (record.SchemaVersion is not ("1" or "2") || record.SourceId == Guid.Empty ||
                    !string.Equals(Path.GetFileName(dir), record.SourceId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(record.ContentSha256 ?? "", "^[0-9a-fA-F]{64}$") || record.Extractor is null ||
                    string.IsNullOrWhiteSpace(record.Extractor.Status) || record.Aliases is null ||
                    string.IsNullOrWhiteSpace(record.OriginalName) || record.ImportedAt == default || record.LastSeenAt == default)
                    throw new InvalidDataException("source.json contains invalid schema or identity fields");
                if (record.Access is not ("local_only" or "unclassified" or "external_allowed")) throw new InvalidDataException("source.json contains an unknown access label");
                var utcImportedAt = record.ImportedAt.ToUniversalTime(); var utcLastSeenAt = record.LastSeenAt.ToUniversalTime();
                if (utcImportedAt != record.ImportedAt || utcLastSeenAt != record.LastSeenAt)
                    record = record with { ImportedAt = utcImportedAt, LastSeenAt = utcLastSeenAt };
                var original = record.OriginalRelativePath;
                if (string.IsNullOrWhiteSpace(original))
                {
                    var actual = Directory.EnumerateFiles(dir, "original.*").FirstOrDefault()
                        ?? Directory.EnumerateFiles(dir, "original").FirstOrDefault();
                    if (actual is not null)
                    {
                        original = Path.GetRelativePath(dir, actual);
                        record = record with { OriginalRelativePath = original };
                    }
                }
                if (string.IsNullOrWhiteSpace(original) || Path.IsPathRooted(original) || original.Split('/', '\\').Contains(".."))
                    throw new InvalidDataException("original_relative_path is missing or unsafe");
                var fullOriginal = Path.GetFullPath(Path.Combine(dir, original));
                var sourceDirectory = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                if (!fullOriginal.StartsWith(sourceDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(fullOriginal))
                    throw new InvalidDataException("Original file referenced by metadata is missing or outside the source directory");
            }
            catch (Exception ex) { problems?.Add($"{Path.GetFileName(dir)}: {ex.Message}"); record = null; }
            if (record is not null) yield return (record, dir);
        }
    }
    public static void SaveRecord(string dir, SourceRecord record)
    {
        var destination = Path.Combine(dir, "source.json"); var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json) + "\n", new UTF8Encoding(false));
        File.Move(temp, destination, true);
    }
}

public sealed class SearchIndex
    : ISearchIndex
{
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private readonly string database;
    public SearchIndex(string root) => database = Path.Combine(root, "index", "search.sqlite");
    public string DatabasePath => database;
    private SqliteConnection Open(string? path = null)
    {
        var c = new SqliteConnection($"Data Source={path ?? database};Pooling=False"); c.Open(); return c;
    }
    private static void CreateSchema(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE sources(source_id TEXT PRIMARY KEY,original_name TEXT,source_type TEXT,imported_at TEXT,source_path TEXT,status TEXT); CREATE VIRTUAL TABLE search USING fts5(source_id UNINDEXED,original_name,source_type,body,tokenize='unicode61'); CREATE TABLE knowledge_records(record_id TEXT PRIMARY KEY,title TEXT,type TEXT,status TEXT,access TEXT,source_ids TEXT); CREATE VIRTUAL TABLE knowledge_search USING fts5(record_id UNINDEXED,title,body,tokenize='unicode61');";
        cmd.ExecuteNonQuery();
    }
    public void Ensure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        if (File.Exists(database)) return;
        using var c = Open(); CreateSchema(c);
    }
    public void InitializeEmpty() => Ensure();
    public void Index(SourceRecord r, string body)
    {
        Ensure(); using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO sources VALUES($id,$name,$type,$date,$path,$status); DELETE FROM search WHERE source_id=$id; INSERT INTO search(source_id,original_name,source_type,body) VALUES($id,$name,$type,$body)";
        Bind(cmd, r, body); cmd.ExecuteNonQuery(); tx.Commit();
    }
    private void EnsureKnowledgeSchema(bool create = false)
    {
        if (!File.Exists(database)) throw new InvalidDataException("Search index is missing; run 'kb rebuild-index'");
        using var c = Open(); using var check = c.CreateCommand(); check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN ('knowledge_records','knowledge_search')";
        if (Convert.ToInt32(check.ExecuteScalar()) == 2) return;
        if (!create) throw new InvalidDataException("Knowledge index schema is missing; run 'kb rebuild-index'");
        using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS knowledge_records(record_id TEXT PRIMARY KEY,title TEXT,type TEXT,status TEXT,access TEXT,source_ids TEXT); CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_search USING fts5(record_id UNINDEXED,title,body,tokenize='unicode61');"; cmd.ExecuteNonQuery();
    }
    public void IndexKnowledge(KnowledgeRecordView record)
    {
        EnsureKnowledgeSchema(create: true); using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM knowledge_records WHERE record_id=$id; DELETE FROM knowledge_search WHERE record_id=$id; INSERT INTO knowledge_records VALUES($id,$title,$type,$status,$access,$sources); INSERT INTO knowledge_search VALUES($id,$title,$body)";
        cmd.Parameters.AddWithValue("$id", record.Metadata.Id.ToString()); cmd.Parameters.AddWithValue("$title", record.Metadata.Title); cmd.Parameters.AddWithValue("$type", record.Metadata.Type);
        cmd.Parameters.AddWithValue("$status", record.Metadata.Status); cmd.Parameters.AddWithValue("$access", record.Metadata.Access); cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(record.Metadata.SourceRefs.Select(x => x.SourceId)));
        cmd.Parameters.AddWithValue("$body", record.Body); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public IReadOnlyList<KnowledgeSearchResult> SearchKnowledge(string query, int limit, string? status)
    {
        EnsureKnowledgeSchema(); using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT r.record_id,r.title,r.type,r.status,r.access,snippet(knowledge_search,2,'[',']','…',18),r.source_ids FROM knowledge_search JOIN knowledge_records r ON r.record_id=knowledge_search.record_id WHERE knowledge_search MATCH $q AND ($status IS NULL OR r.status=$status) ORDER BY bm25(knowledge_search) LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", query); cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var results = new List<KnowledgeSearchResult>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), JsonSerializer.Deserialize<Guid[]>(reader.GetString(6)) ?? Array.Empty<Guid>()));
        return results;
    }
    public IReadOnlyList<string> VerifyKnowledge(IReadOnlyCollection<KnowledgeRecordView> records)
    {
        if (!File.Exists(database)) return new[] { "index: missing_knowledge_index" };
        var errors = new List<string>();
        try
        {
            using var c = Open(); using var metadata = c.CreateCommand(); metadata.CommandText = "SELECT record_id,title,type,status,access,source_ids FROM knowledge_records";
            var rows = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
            using (var reader = metadata.ExecuteReader()) while (reader.Read()) { var id = reader.GetString(0); if (!rows.TryGetValue(id, out var list)) rows[id] = list = new(); list.Add(Enumerable.Range(1, 5).Select(reader.GetString).ToArray()); }
            using var fts = c.CreateCommand(); fts.CommandText = "SELECT record_id,title,body FROM knowledge_search";
            var ftsRows = new Dictionary<string, List<(string Title, string Body)>>(StringComparer.OrdinalIgnoreCase);
            using (var reader = fts.ExecuteReader()) while (reader.Read()) { var id = reader.GetString(0); if (!ftsRows.TryGetValue(id, out var list)) ftsRows[id] = list = new(); list.Add((reader.GetString(1), reader.GetString(2))); }
            var expectedIds = records.Select(x => x.Metadata.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                var id = record.Metadata.Id.ToString(); var expectedSources = JsonSerializer.Serialize(record.Metadata.SourceRefs.Select(x => x.SourceId));
                if (!rows.TryGetValue(id, out var rowList) || rowList.Count != 1 || !ftsRows.TryGetValue(id, out var ftsList) || ftsList.Count != 1 ||
                    rowList[0][0] != record.Metadata.Title || rowList[0][1] != record.Metadata.Type || rowList[0][2] != record.Metadata.Status || rowList[0][3] != record.Metadata.Access || rowList[0][4] != expectedSources ||
                    ftsList[0].Title != record.Metadata.Title || ftsList[0].Body != record.Body) errors.Add($"{id}: knowledge_index_mismatch");
            }
            foreach (var id in rows.Keys.Union(ftsRows.Keys, StringComparer.OrdinalIgnoreCase).Where(x => !expectedIds.Contains(x))) errors.Add($"{id}: knowledge_index_extra");
        }
        catch (Exception ex) { errors.Add($"index: invalid_knowledge_index ({ex.Message})"); }
        return errors;
    }
    private static void Bind(SqliteCommand cmd, SourceRecord r, string body)
    {
        cmd.Parameters.AddWithValue("$id", r.SourceId.ToString()); cmd.Parameters.AddWithValue("$name", r.OriginalName);
        cmd.Parameters.AddWithValue("$type", r.SourceType); cmd.Parameters.AddWithValue("$date", r.ImportedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$path", $"sources/{r.SourceId}/{(r.OriginalRelativePath ?? "original.bin").Replace('\\', '/')}");
        cmd.Parameters.AddWithValue("$status", r.Extractor.Status); cmd.Parameters.AddWithValue("$body", body);
    }
    public (int Indexed, int Skipped, int KnowledgeIndexed) Rebuild(IEnumerable<(SourceRecord Record, string Directory)> sources, int expected, IReadOnlyCollection<KnowledgeRecordView>? knowledge = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var temp = database + ".rebuild-" + Guid.NewGuid().ToString("N"); int indexed = 0, skipped = 0;
        try
        {
            using (var c = Open(temp))
            {
                CreateSchema(c);
                foreach (var (record, dir) in sources)
                {
                    var textPath = Path.Combine(dir, "extracted.txt");
                    if (record.Extractor.Status == "indexed" && !File.Exists(textPath)) throw new InvalidDataException($"{record.SourceId}: extracted_text_missing");
                    var text = File.Exists(textPath) ? File.ReadAllText(textPath, Encoding.UTF8) : "";
                    if (record.Extractor.Status == "indexed" && text.Length == 0) throw new InvalidDataException($"{record.SourceId}: extracted_text_empty");
                    if (record.Extractor.Status == "indexed" && (record.ExtractedTextSha256 is null || !string.Equals(HashText(text), record.ExtractedTextSha256, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException($"{record.SourceId}: extracted_text_hash_mismatch_or_missing");
                    if (record.Extractor.Status == "indexed" && text.Length > 0) indexed++; else skipped++;
                    using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO sources VALUES($id,$name,$type,$date,$path,$status); INSERT INTO search(source_id,original_name,source_type,body) VALUES($id,$name,$type,$body)";
                    Bind(cmd, record, text); cmd.ExecuteNonQuery(); tx.Commit();
                }
                foreach (var record in knowledge ?? Array.Empty<KnowledgeRecordView>())
                {
                    using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO knowledge_records VALUES($id,$title,$type,$status,$access,$sources); INSERT INTO knowledge_search VALUES($id,$title,$body)";
                    cmd.Parameters.AddWithValue("$id", record.Metadata.Id.ToString()); cmd.Parameters.AddWithValue("$title", record.Metadata.Title); cmd.Parameters.AddWithValue("$type", record.Metadata.Type);
                    cmd.Parameters.AddWithValue("$status", record.Metadata.Status); cmd.Parameters.AddWithValue("$access", record.Metadata.Access); cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(record.Metadata.SourceRefs.Select(x => x.SourceId)));
                    cmd.Parameters.AddWithValue("$body", record.Body); cmd.ExecuteNonQuery(); tx.Commit();
                }
                using var check = c.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQLite integrity_check failed");
                using var count = c.CreateCommand(); count.CommandText = "SELECT count(*) FROM sources";
                if (Convert.ToInt32(count.ExecuteScalar()) != expected) throw new InvalidDataException("Rebuilt index source count does not match valid source metadata");
                using var knowledgeCount = c.CreateCommand(); knowledgeCount.CommandText = "SELECT count(*) FROM knowledge_records";
                if (Convert.ToInt32(knowledgeCount.ExecuteScalar()) != (knowledge?.Count ?? 0)) throw new InvalidDataException("Rebuilt index knowledge count does not match valid knowledge records");
            }
            File.Move(temp, database, true); return (indexed, skipped, knowledge?.Count ?? 0);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public (string Integrity, int Missing, int Extra, int Count) Verify(IReadOnlyCollection<SourceRecord> records)
    {
        if (!File.Exists(database)) return ("missing", records.Count, 0, 0);
        try
        {
            using var c = Open(); using var check = c.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
            var integrity = Convert.ToString(check.ExecuteScalar()) ?? "unknown";
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT source_id,original_name,source_type,imported_at,source_path,status FROM sources";
            var rows = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) { var id = reader.GetString(0); if (!rows.TryGetValue(id, out var list)) rows[id] = list = new(); list.Add(Enumerable.Range(1, 5).Select(reader.GetString).ToArray()); }
            var expected = records.ToDictionary(r => r.SourceId.ToString(), StringComparer.OrdinalIgnoreCase);
            using var ftsCommand = c.CreateCommand(); ftsCommand.CommandText = "SELECT source_id,original_name,source_type,body FROM search";
            var ftsRows = new Dictionary<string, List<(string Name, string Type, string Body)>>(StringComparer.OrdinalIgnoreCase);
            using (var reader = ftsCommand.ExecuteReader()) while (reader.Read()) { var id = reader.GetString(0); if (!ftsRows.TryGetValue(id, out var list)) ftsRows[id] = list = new(); list.Add((reader.GetString(1), reader.GetString(2), reader.GetString(3))); }
            var allIndexed = rows.Keys.Union(ftsRows.Keys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingIds = expected.Where(pair => !rows.TryGetValue(pair.Key, out var rowList) || rowList.Count != 1 || !ftsRows.TryGetValue(pair.Key, out var ftsList) || ftsList.Count != 1 ||
                !RowMatches(pair.Value, rowList[0]) || ftsList[0].Name != pair.Value.OriginalName || ftsList[0].Type != pair.Value.SourceType).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                var dir = Path.Combine(Path.GetDirectoryName(database)!, "..", "sources", record.SourceId.ToString());
                var original = Path.Combine(dir, record.OriginalRelativePath ?? "original.bin");
                if (!File.Exists(original) || !string.Equals(HashFile(original), record.ContentSha256, StringComparison.OrdinalIgnoreCase)) missingIds.Add(record.SourceId.ToString());
                if (record.Extractor.Status == "indexed")
                {
                    var textPath = Path.Combine(dir, "extracted.txt");
                    var text = File.Exists(textPath) ? File.ReadAllText(textPath, Encoding.UTF8) : null;
                    if (text is null || record.ExtractedTextSha256 is null || !string.Equals(HashText(text), record.ExtractedTextSha256, StringComparison.OrdinalIgnoreCase) || !ftsRows.TryGetValue(record.SourceId.ToString(), out var bodies) || bodies.Count != 1 || bodies[0].Body != text) missingIds.Add(record.SourceId.ToString());
                }
            }
            return (integrity, missingIds.Count, allIndexed.Except(expected.Keys, StringComparer.OrdinalIgnoreCase).Count(), rows.Count);
        }
        catch (Exception ex) { return ($"invalid: {ex.Message}", records.Count, 0, 0); }

        static bool RowMatches(SourceRecord record, string[] row) =>
            row[0] == record.OriginalName && row[1] == record.SourceType &&
            row[2] == record.ImportedAt.ToUniversalTime().ToString("O") &&
            row[3] == $"sources/{record.SourceId}/{(record.OriginalRelativePath ?? "original.bin").Replace('\\', '/')}" &&
            row[4] == record.Extractor.Status;
        static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    }
    public IReadOnlyList<SearchResult> Search(string query, int limit, string? sourceType, DateTimeOffset? from, DateTimeOffset? to, string? status)
    {
        if (!File.Exists(database)) throw new InvalidDataException("Search index is missing; run 'kb rebuild-index'"); using var connection = Open(); using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT s.source_id,s.original_name,s.source_type,s.imported_at,s.source_path,s.status,snippet(search,3,'[',']','…',18) FROM search JOIN sources s ON s.source_id=search.source_id WHERE search MATCH $q AND ($type IS NULL OR s.source_type=$type) AND ($from IS NULL OR s.imported_at >= $from) AND ($to IS NULL OR s.imported_at < $to) AND ($status IS NULL OR s.status=$status) ORDER BY bm25(search),s.imported_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", query); cmd.Parameters.AddWithValue("$type", (object?)sourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)from?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var results = new List<SearchResult>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(6), reader.GetString(5)));
        return results;
    }
}

public interface ISearchIndex
{
    string DatabasePath { get; }
    void Ensure();
    void Index(SourceRecord record, string body);
    (int Indexed, int Skipped, int KnowledgeIndexed) Rebuild(IEnumerable<(SourceRecord Record, string Directory)> sources, int expected, IReadOnlyCollection<KnowledgeRecordView>? knowledge = null);
    void IndexKnowledge(KnowledgeRecordView record);
    IReadOnlyList<KnowledgeSearchResult> SearchKnowledge(string query, int limit, string? status);
    IReadOnlyList<string> VerifyKnowledge(IReadOnlyCollection<KnowledgeRecordView> records);
    (string Integrity, int Missing, int Extra, int Count) Verify(IReadOnlyCollection<SourceRecord> records);
    IReadOnlyList<SearchResult> Search(string query, int limit, string? sourceType, DateTimeOffset? from, DateTimeOffset? to, string? status);
}

public sealed class KnowledgeBaseStore
{
    private const long MaxSupportedFileBytes = 2L * 1024 * 1024 * 1024;
    private readonly string root;
    private readonly SourceRepository repository;
    private readonly ISearchIndex index;
    private readonly ITextExtractor[] extractors;
    private readonly KnowledgeRepository knowledge;
    private string SettingsPath => Path.Combine(root, "config", "settings.json");
    public KnowledgeBaseStore(string path, IEnumerable<ITextExtractor>? textExtractors = null, ISearchIndex? searchIndex = null)
    {
        root = Path.GetFullPath(path); repository = new(root); index = searchIndex ?? new SearchIndex(root);
        extractors = (textExtractors ?? new ITextExtractor[] { new PdfTextExtractor(), new Utf8TextExtractor() }).ToArray();
        knowledge = new KnowledgeRepository(root, Show, _ => AcquireLock());
    }
    public void Initialize()
    {
        Directory.CreateDirectory(repository.SourcesPath); Directory.CreateDirectory(Path.Combine(root, "index")); Directory.CreateDirectory(Path.Combine(root, "config"));
        if (!File.Exists(SettingsPath)) File.WriteAllText(SettingsPath, "{\"schema_version\":\"2\",\"max_file_size_bytes\":52428800,\"max_extracted_text_bytes\":10485760}\n");
        if (!Directory.EnumerateDirectories(repository.SourcesPath).Any(d => !Path.GetFileName(d).StartsWith(".tmp-", StringComparison.Ordinal))) index.Ensure();
    }
    private long ReadMaxFileSize()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var obj = doc.RootElement;
        if (obj.ValueKind != JsonValueKind.Object) throw new InvalidDataException("settings.json must contain a JSON object");
        if (!obj.TryGetProperty("schema_version", out var version) || version.GetString() is not ("1" or "2" or "3")) throw new InvalidDataException("settings.json has an unsupported schema_version");
        if (!obj.TryGetProperty("max_file_size_bytes", out var size) || !size.TryGetInt64(out var value) || value <= 0 || value > MaxSupportedFileBytes)
            throw new InvalidDataException($"settings.json max_file_size_bytes must be between 1 and {MaxSupportedFileBytes}");
        return value;
    }
    private long ReadMaxExtractedTextBytes()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var obj = doc.RootElement;
        if (obj.TryGetProperty("max_extracted_text_bytes", out var size))
        {
            if (size.TryGetInt64(out var value) && value > 0 && value <= MaxSupportedFileBytes) return value;
            throw new InvalidDataException($"settings.json max_extracted_text_bytes must be between 1 and {MaxSupportedFileBytes}");
        }
        return 10L * 1024 * 1024;
    }
    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(root); var path = Path.Combine(root, ".operation.lock"); var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { if (DateTime.UtcNow >= until) throw new IOException("Knowledge base is busy; another import or index rebuild holds the lock"); Thread.Sleep(100); }
        }
    }
    public ImportSummary Import(string inputPath)
    {
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath)) throw new FileNotFoundException("Input path does not exist", inputPath);
        using var operation = AcquireLock(); Initialize(); var maxSize = ReadMaxFileSize(); var maxTextSize = ReadMaxExtractedTextBytes(); var fullInput = Path.GetFullPath(inputPath);
        if (string.Equals(fullInput.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Cannot import the knowledge-base root");
        if (IsWithin(fullInput, root)) throw new InvalidDataException("Cannot import files from inside the knowledge base");
        var skippedLinks = 0; var items = new List<ImportItem>();
        string[] files;
        if (File.Exists(fullInput) && (File.GetAttributes(fullInput) & FileAttributes.ReparsePoint) != 0) { files = Array.Empty<string>(); skippedLinks = 1; }
        else if (File.Exists(fullInput)) files = new[] { fullInput };
        else files = EnumerateFiles(fullInput, out skippedLinks, items).Where(f => !IsWithin(f, root)).ToArray();
        var byHash = new Dictionary<string, (SourceRecord Record, string Directory)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in repository.ReadSources()) byHash.TryAdd(source.Record.ContentSha256, source);
        int imported = 0, duplicates = 0, indexed = 0, unsupported = 0, errors = items.Count, oversized = 0;
        foreach (var file in files)
        {
            var relative = File.Exists(fullInput) ? Path.GetFileName(file) : Path.GetRelativePath(fullInput, file).Replace(Path.DirectorySeparatorChar, '/');
            try
            {
                var info = new FileInfo(file); if (info.Length > maxSize) { oversized++; items.Add(new(relative, "oversized", Message: $"File exceeds configured limit ({maxSize} bytes)")); continue; }
                var sourceHash = HashFile(file);
                if (byHash.TryGetValue(sourceHash, out var existing))
                {
                    var aliases = existing.Record.Aliases.Append(relative).Distinct(StringComparer.Ordinal).ToArray();
                    var updated = existing.Record with { LastSeenAt = DateTimeOffset.UtcNow, Aliases = aliases };
                    SourceRepository.SaveRecord(existing.Directory, updated); byHash[sourceHash] = (updated, existing.Directory); duplicates++;
                    try
                    {
                        var textPath = Path.Combine(existing.Directory, "extracted.txt");
                        index.Index(updated, File.Exists(textPath) ? File.ReadAllText(textPath) : "");
                        items.Add(new(relative, "duplicate", updated.SourceId));
                    }
                    catch (Exception ex) { errors++; items.Add(new(relative, "index_error", updated.SourceId, "Source exists; search index needs rebuilding", ex.Message)); }
                    continue;
                }
                var id = Guid.NewGuid(); var temp = Path.Combine(repository.SourcesPath, ".tmp-" + id.ToString("N")); Directory.CreateDirectory(temp);
                try
                {
                    var ext = SafeExtension(file); var originalRelative = "original" + ext; var original = Path.Combine(temp, originalRelative);
                    var copiedHash = CopyAndHash(file, original);
                    if (!string.Equals(sourceHash, copiedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("source_changed_during_import");
                    var extraction = extractors.FirstOrDefault(x => x.Supports(file))?.Extract(original) ?? new(null, "none", "unsupported");
                    if (extraction.Text is not null)
                    {
                        extraction = extraction with { Text = NormalizeLf(extraction.Text) };
                        if (Encoding.UTF8.GetByteCount(extraction.Text) > maxTextSize) extraction = new(null, extraction.Name, "text_limit_exceeded", $"Extracted text exceeds configured limit ({maxTextSize} bytes)");
                        if (extraction.Text is not null) File.WriteAllText(Path.Combine(temp, "extracted.txt"), extraction.Text, new UTF8Encoding(false));
                    }
                    var now = DateTimeOffset.UtcNow; var record = new SourceRecord("1", id, copiedHash, Path.GetFileName(file), "file", Mime(file), now, now, relative,
                        new(extraction.Name, "1", extraction.Status, extraction.Status == "extraction_error" ? (extraction.Name == "pdfpig" ? "PDF could not be read" : "Text could not be read") : extraction.Error),
                        "unclassified", Array.Empty<string>(), originalRelative, extraction.Text is null ? null : HashText(extraction.Text));
                    SourceRepository.SaveRecord(temp, record); var final = repository.SourcePath(id); Directory.Move(temp, final); imported++;
                    if (record.Extractor.Status == "indexed") indexed++;
                    if (record.Extractor.Status == "unsupported") unsupported++;
                    if (record.Extractor.Status == "extraction_error") errors++;
                    byHash[copiedHash] = (record, final);
                    try { index.Index(record, extraction.Text ?? ""); items.Add(new(relative, record.Extractor.Status, id, Details: extraction.Error)); }
                    catch (Exception ex) { errors++; items.Add(new(relative, "index_error", id, "Source saved; search index needs rebuilding", ex.Message)); }
                    if (record.Extractor.Status == "extraction_error" && items[^1].Status == "extraction_error")
                        items[^1] = items[^1] with { Message = record.Extractor.Error };
                }
                finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
            }
            catch (Exception ex)
            {
                errors++; var status = ex.Message == "source_changed_during_import" ? "source_changed_during_import" : "error";
                items.Add(new(relative, status, Message: status == "error" ? "File could not be imported" : status, Details: ex.Message));
            }
        }
        return new(imported, duplicates, indexed, unsupported, errors, oversized, skippedLinks, items);
    }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string NormalizeLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static bool IsWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path); var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    private static string CopyAndHash(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
        using var sha = SHA256.Create(); var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { output.Write(buffer, 0, read); sha.TransformBlock(buffer, 0, read, null, 0); }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); output.Flush(true); return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }
    private static string SafeExtension(string path)
    {
        var ext = Path.GetExtension(path); return string.IsNullOrWhiteSpace(ext) || ext.Length > 20 || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? ".bin" : ext;
    }
    private static IEnumerable<string> EnumerateFiles(string rootPath, out int skippedLinks, List<ImportItem> errors)
    {
        var result = new List<string>(); var skipped = 0; var patterns = new List<string>(); var ignorePath = Path.Combine(rootPath, ".kbignore");
        try { if (File.Exists(ignorePath)) patterns.AddRange(File.ReadAllLines(ignorePath).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#'))); }
        catch (Exception ex) { errors.Add(new(".kbignore", "error", Message: "Ignore rules could not be read", Details: ex.Message)); }
        bool Ignored(string rel) => patterns.Any(p => GlobMatch(p, rel));
        var builtins = new HashSet<string>(new[] { ".git", ".vs", "bin", "obj", "node_modules", "packages" }, StringComparer.OrdinalIgnoreCase);
        void Error(string path, Exception ex) => errors.Add(new(Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/'), "error", Message: "Path could not be scanned", Details: ex.Message));
        void Walk(string dir)
        {
            string[] files, directories;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) { Error(dir, ex); return; }
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(rootPath, file).Replace(Path.DirectorySeparatorChar, '/');
                if (Path.GetFileName(file) == ".kbignore" || Ignored(rel)) continue;
                try { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; } }
                catch (Exception ex) { Error(file, ex); continue; }
                result.Add(file);
            }
            try { directories = Directory.GetDirectories(dir); }
            catch (Exception ex) { Error(dir, ex); return; }
            foreach (var sub in directories)
            {
                var rel = Path.GetRelativePath(rootPath, sub).Replace(Path.DirectorySeparatorChar, '/');
                try { if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; } }
                catch (Exception ex) { Error(sub, ex); continue; }
                if (builtins.Contains(Path.GetFileName(sub)) || Ignored(rel) || patterns.Any(p => GlobMatch(p.TrimEnd('/') + "/**", rel + "/"))) continue;
                Walk(sub);
            }
        }
        try { if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0) skipped++; else Walk(rootPath); }
        catch (Exception ex) { Error(rootPath, ex); }
        skippedLinks = skipped; return result;
    }
    private static bool GlobMatch(string pattern, string path)
    {
        pattern = pattern.Replace('\\', '/').TrimStart('/'); path = path.Replace('\\', '/');
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') { regex.Append("(?:.*/)?"); i++; }
                else regex.Append(".*");
            }
            else if (pattern[i] == '*') regex.Append("[^/]*");
            else if (pattern[i] == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(pattern[i].ToString()));
        }
        regex.Append('$');
        return Regex.IsMatch(path, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    private static string? Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch { ".txt" => "text/plain", ".md" => "text/markdown", ".html" or ".htm" => "text/html", ".json" => "application/json", ".csv" => "text/csv", ".pdf" => "application/pdf", _ => null };
    public IReadOnlyList<SearchResult> Search(string query, int limit = 10, string? sourceType = null, DateTimeOffset? from = null, DateTimeOffset? to = null, string? status = null)
    {
        var paths = repository.ReadSources().ToDictionary(x => x.Record.SourceId, x => $"sources/{x.Record.SourceId}/{(x.Record.OriginalRelativePath ?? "original.bin").Replace('\\', '/')}");
        return index.Search(query, limit, sourceType, from, to, status)
            .Select(result => paths.TryGetValue(result.SourceId, out var path) ? result with { SourcePath = path } : result).ToArray();
    }
    public (SourceRecord Record, string Directory) Show(Guid id)
    {
        var hit = repository.ReadSources().FirstOrDefault(x => x.Record.SourceId == id);
        if (hit.Record is null) throw new FileNotFoundException($"Source {id} was not found");
        return hit;
    }
    public object Status(bool verify = false)
    {
        var problems = new List<string>(); var records = repository.ReadSources(problems).Select(x => x.Record).ToArray();
        var check = verify ? index.Verify(records) : ("not_checked", 0, 0, 0);
        var integrityErrors = new List<string>(problems);
        if (verify)
        {
            foreach (var (record, dir) in repository.ReadSources())
            {
                var original = Path.Combine(dir, record.OriginalRelativePath ?? "original.bin");
                if (!File.Exists(original) || !string.Equals(HashFile(original), record.ContentSha256, StringComparison.OrdinalIgnoreCase)) integrityErrors.Add($"{record.SourceId}: original_hash_mismatch");
                if (record.Extractor.Status == "indexed")
                {
                    var text = Path.Combine(dir, "extracted.txt");
                    if (!File.Exists(text)) integrityErrors.Add($"{record.SourceId}: extracted_text_missing");
                    else if (record.ExtractedTextSha256 is null || !string.Equals(HashFile(text), record.ExtractedTextSha256, StringComparison.OrdinalIgnoreCase)) integrityErrors.Add($"{record.SourceId}: extracted_text_hash_mismatch");
                }
            }
            if (check.Item1 != "ok") integrityErrors.Add($"index: {check.Item1}");
            if (check.Item2 != 0) integrityErrors.Add($"index: {check.Item2} missing_or_duplicate_rows");
            if (check.Item3 != 0) integrityErrors.Add($"index: {check.Item3} extra_rows");
            try
            {
                var knowledgeRecords = knowledge.List();
                integrityErrors.AddRange(knowledgeRecords.Where(x => x.StaleReason is not null).Select(x => $"{x.Metadata.Id}: stale ({x.StaleReason})"));
                var knowledgeIds = knowledgeRecords.Select(x => x.Metadata.Id).ToHashSet();
                foreach (var record in knowledgeRecords)
                    foreach (var relation in record.Metadata.RelatedRecords ?? Array.Empty<RelatedRecord>())
                        if (!knowledgeIds.Contains(relation.RecordId)) integrityErrors.Add($"{record.Metadata.Id}: related_record_missing ({relation.RecordId})");
                integrityErrors.AddRange(knowledge.VerifyHistory());
                integrityErrors.AddRange(index.VerifyKnowledge(knowledgeRecords));
            }
            catch (Exception ex) { integrityErrors.Add($"knowledge: invalid_record ({ex.Message})"); }
        }
        return new
        {
            schema_version = "1",
            index_exists = File.Exists(index.DatabasePath),
            total = records.Length,
            statuses = records.GroupBy(x => x.Extractor.Status).ToDictionary(g => g.Key, g => g.Count()),
            invalid_metadata = problems.Count,
            metadata_errors = problems,
            index_integrity = check.Item1,
            index_missing_sources = check.Item2,
            index_extra_sources = check.Item3,
            index_records = check.Item4,
            verify_ok = !verify || integrityErrors.Count == 0,
            verify_errors = verify ? (IReadOnlyList<string>)integrityErrors : Array.Empty<string>(),
            recent_errors = records.Where(x => x.Extractor.Error is not null).TakeLast(10).Select(x => new { x.SourceId, x.OriginalName, x.Extractor.Error })
        };
    }
    public RebuildResult Rebuild()
    {
        using var operation = AcquireLock(); Initialize(); knowledge.RecoverPendingTransitions(); var problems = new List<string>(); var records = repository.ReadSources(problems).ToArray();
        if (problems.Count > 0) throw new InvalidDataException("Cannot rebuild index because source metadata is invalid: " + string.Join("; ", problems));
        var knowledgeRecords = knowledge.ListCanonical();
        var result = index.Rebuild(records, records.Length, knowledgeRecords);
        return new RebuildResult(result.Indexed, result.Skipped, result.KnowledgeIndexed);
    }

    public object Migrate(int targetVersion)
    {
        if (targetVersion == 3) return Migrate3();
        if (targetVersion != 2) throw new ArgumentOutOfRangeException(nameof(targetVersion), "Only migration targets 2 and 3 are supported");
        using var operation = AcquireLock();
        RecoverMigration();
        Initialize();
        var problems = new List<string>(); var records = repository.ReadSources(problems).ToArray();
        if (problems.Count > 0) throw new InvalidDataException("Cannot migrate invalid metadata: " + string.Join("; ", problems));
        var currentSettingsVersion = File.Exists(SettingsPath) ? JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.GetProperty("schema_version").GetString() : "1";
        if (records.All(x => x.Record.SchemaVersion == "2") && currentSettingsVersion == "2") return new { schema_version = "2", migrated_sources = 0, backups = Array.Empty<string>(), already_current = true };
        var missingText = records.Where(x => x.Record.Extractor.Status == "indexed" && !File.Exists(Path.Combine(x.Directory, "extracted.txt"))).Select(x => $"{x.Record.SourceId}: extracted_text_missing").ToArray();
        if (missingText.Length > 0) throw new InvalidDataException("Cannot migrate indexed sources without extracted text: " + string.Join("; ", missingText));
        var backups = new List<string>(); var changes = new List<MigrationChange>();
        foreach (var (record, dir) in records)
        {
            var metadata = Path.Combine(dir, "source.json");
            var backup = metadata + ".v1.bak"; _ = SafeMigrationPath(Path.GetRelativePath(root, metadata)); _ = SafeMigrationPath(Path.GetRelativePath(root, backup));
            if (!File.Exists(backup)) { File.Copy(metadata, backup); backups.Add(backup); }
            var updated = record with { SchemaVersion = "2" };
            var textPath = Path.Combine(dir, "extracted.txt"); string? hash = null;
            if (record.Extractor.Status == "indexed" && File.Exists(textPath))
            {
                var textBackup = textPath + ".v1.bak"; _ = SafeMigrationPath(Path.GetRelativePath(root, textPath)); _ = SafeMigrationPath(Path.GetRelativePath(root, textBackup));
                if (!File.Exists(textBackup)) { File.Copy(textPath, textBackup); backups.Add(textBackup); }
                var normalized = NormalizeLf(File.ReadAllText(textPath, Encoding.UTF8)); hash = HashText(normalized);
                changes.Add(PrepareMigrationChange(textPath, normalized, "extracted"));
            }
            updated = updated with { ExtractedTextSha256 = hash };
            changes.Add(PrepareMigrationChange(metadata, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + "\n", "metadata"));
        }
        if (File.Exists(SettingsPath)) { var settingsBackup = SettingsPath + ".v1.bak"; _ = SafeMigrationPath(Path.GetRelativePath(root, SettingsPath)); _ = SafeMigrationPath(Path.GetRelativePath(root, settingsBackup)); if (!File.Exists(settingsBackup)) { File.Copy(SettingsPath, settingsBackup); backups.Add(settingsBackup); } }
        var settings = new { schema_version = "2", max_file_size_bytes = ReadMaxFileSize(), max_extracted_text_bytes = ReadMaxExtractedTextBytes() };
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        changes.Add(PrepareMigrationChange(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + "\n", "settings"));
        var journalPath = MigrationJournalPath;
        WriteMigrationJournal(journalPath, changes, 0);
        CommitMigration(journalPath, changes, 0);
        return new { schema_version = "2", migrated_sources = records.Count(x => x.Record.SchemaVersion != "2"), backups, entries = changes.Select(x => new { x.Kind, x.Target, x.Sha256 }) };
    }

    private object Migrate3()
    {
        using var operation = AcquireLock(); RecoverMigration(); Initialize(); knowledge.RecoverPendingTransitions();
        var problems = new List<string>(); var records = repository.ReadSources(problems).ToArray();
        if (problems.Count > 0) throw new InvalidDataException("Cannot migrate invalid metadata: " + string.Join("; ", problems));
        if (records.Any(x => x.Record.SchemaVersion != "2")) throw new InvalidDataException("Run 'kb migrate --to 2' before migration 3");
        using var settingsDoc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var settingsVersion = settingsDoc.RootElement.GetProperty("schema_version").GetString();
        if (settingsVersion == "3" && knowledge.ListCanonical().All(x => x.Metadata.SchemaVersion == "3"))
            return new { schema_version = "3", migrated_records = 0, migrated_sources = 0, policy_revision = ReadPolicyRevision(), already_current = true };
        if (settingsVersion != "2") throw new InvalidDataException("Settings must be schema 2 before migration 3");
        var migrated = knowledge.MigrateToSchema3();
        var policyPath = Path.Combine(root, "config", "access-policy.json");
        if (!File.Exists(policyPath)) File.WriteAllText(policyPath, "{\"schema_version\":\"1\",\"revision\":1}\n", new UTF8Encoding(false));
        var settings = new { schema_version = "3", max_file_size_bytes = ReadMaxFileSize(), max_extracted_text_bytes = ReadMaxExtractedTextBytes() };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) + "\n", new UTF8Encoding(false));
        return new { schema_version = "3", migrated_records = migrated, migrated_sources = 0, policy_revision = ReadPolicyRevision() };
    }

    private string MigrationJournalPath => Path.Combine(root, ".migration-v2.json");
    private MigrationChange PrepareMigrationChange(string target, string content, string kind)
    {
        var staged = target + ".v2.new"; _ = SafeMigrationPath(Path.GetRelativePath(root, target)); _ = SafeMigrationPath(Path.GetRelativePath(root, staged)); File.WriteAllText(staged, content, new UTF8Encoding(false));
        return new MigrationChange(Path.GetRelativePath(root, target), Path.GetRelativePath(root, staged), HashText(content), File.Exists(target) ? HashFile(target) : null, kind);
    }
    private sealed record MigrationChange(string Target, string Staged, string Sha256, string? OriginalSha256, string Kind);
    private void WriteMigrationJournal(string path, IReadOnlyList<MigrationChange> changes, int completed)
    {
        _ = SafeMigrationPath(Path.GetRelativePath(root, path));
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        _ = SafeMigrationPath(Path.GetRelativePath(root, temp));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(temp, JsonSerializer.Serialize(new { format_version = 1, phase = "committing", completed, changes }, options), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    private void CommitMigration(string path, IReadOnlyList<MigrationChange> changes, int completed)
    {
        if (completed < 0 || completed > changes.Count) throw new InvalidDataException("Migration journal completed count is outside its valid range");
        for (var i = 0; i < completed; i++)
        {
            var completedTarget = SafeMigrationPath(changes[i].Target);
            if (!File.Exists(completedTarget) || !string.Equals(HashFile(completedTarget), changes[i].Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Completed migration target hash mismatch: {changes[i].Target}; journal and backups were preserved");
        }
        for (var i = completed; i < changes.Count; i++)
        {
            var change = changes[i]; var target = SafeMigrationPath(change.Target); var staged = SafeMigrationPath(change.Staged);
            if (File.Exists(staged))
            {
                if (!string.Equals(HashFile(staged), change.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Staged migration hash mismatch: {change.Staged}; journal and files were preserved");
                if (File.Exists(target) && !string.Equals(HashFile(target), change.OriginalSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Migration target changed unexpectedly: {change.Target}");
                if (!File.Exists(target) && change.OriginalSha256 is not null) throw new InvalidDataException($"Migration target disappeared unexpectedly: {change.Target}");
                File.Move(staged, target, true);
            }
            else if (!File.Exists(target) || !string.Equals(HashFile(target), change.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Migration stopped at {change.Kind}: neither staged file nor target matches expected hash; restore backups and remove {path} after inspection");
            WriteMigrationJournal(path, changes, i + 1);
        }
        File.Delete(path);
    }
    private void RecoverMigration()
    {
        _ = SafeMigrationPath(Path.GetRelativePath(root, MigrationJournalPath));
        if (!File.Exists(MigrationJournalPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(MigrationJournalPath)); var rootElement = document.RootElement;
            if (rootElement.GetProperty("format_version").GetInt32() != 1) throw new InvalidDataException("unsupported migration journal version");
            if (rootElement.GetProperty("phase").GetString() != "committing") throw new InvalidDataException("unsupported migration journal phase");
            var changes = rootElement.GetProperty("changes").EnumerateArray().Select(x => new MigrationChange(x.GetProperty("target").GetString()!, x.GetProperty("staged").GetString()!, x.GetProperty("sha256").GetString()!, x.TryGetProperty("originalSha256", out var original) ? original.GetString() : null, x.GetProperty("kind").GetString()!)).ToArray();
            var completed = rootElement.GetProperty("completed").GetInt32();
            if (completed < 0 || completed > changes.Length) throw new InvalidDataException("invalid completed count");
            CommitMigration(MigrationJournalPath, changes, completed);
        }
        catch (Exception ex) { throw new InvalidDataException($"Migration recovery stopped. Inspect backups (*.v1.bak) and {MigrationJournalPath}: {ex.Message}", ex); }
    }

    public long ReadPolicyRevision()
    {
        var path = Path.Combine(root, "config", "access-policy.json");
        if (!File.Exists(path)) return 1;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("revision", out var value) || !value.TryGetInt64(out var revision) || revision < 1) throw new InvalidDataException("Access policy revision is invalid");
        return revision;
    }
    private void BumpPolicyRevision(string subject, string reason)
    {
        var path = Path.Combine(root, "config", "access-policy.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required", nameof(reason));
        var revision = ReadPolicyRevision() + 1; var changes = new List<JsonElement>();
        if (File.Exists(path))
        {
            using var old = JsonDocument.Parse(File.ReadAllText(path));
            if (old.RootElement.TryGetProperty("changes", out var existing) && existing.ValueKind == JsonValueKind.Array) changes.AddRange(existing.EnumerateArray().Select(x => x.Clone()));
        }
        changes.Add(JsonSerializer.SerializeToElement(new { revision, at = DateTimeOffset.UtcNow, subject, reason }));
        if (changes.Count > 10000) changes.RemoveRange(0, changes.Count - 10000);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(new { schema_version = "1", revision, changes }) + "\n", new UTF8Encoding(false)); File.Move(temp, path, true);
    }
    public long SetSourceAccess(Guid id, string level, string reason)
    {
        if (level is not ("local_only" or "unclassified" or "external_allowed")) throw new ArgumentException("Unsupported access level", nameof(level));
        using var operation = AcquireLock(); var (record, dir) = Show(id);
        if (record.Access == level) return ReadPolicyRevision();
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required", nameof(reason));
        SourceRepository.SaveRecord(dir, record with { Access = level }); BumpPolicyRevision($"source:{id}", reason); return ReadPolicyRevision();
    }
    public long SetRecordAccessOverride(Guid id, string? level, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required", nameof(reason));
        using var operation = AcquireLock(); _ = knowledge.SetAccessOverride(id, level); BumpPolicyRevision($"record:{id}", reason); return ReadPolicyRevision();
    }
    public long PolicyRevision => ReadPolicyRevision();
    public IDisposable AcquirePolicyGuard() => AcquireLock();
    public string SchemaVersion
    {
        get { using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath)); return doc.RootElement.GetProperty("schema_version").GetString() ?? ""; }
    }
    public void RecoverServerState()
    {
        using var operation = AcquireLock(); RecoverMigration(); knowledge.RecoverPendingTransitions();
        var transition = Path.Combine(root, "knowledge", ".record-transition.json");
        if (File.Exists(transition)) knowledge.RecoverPendingTransitions();
    }
    public string EffectiveAccess(KnowledgeRecord metadata)
    {
        if (metadata.SourceRefs.Length == 0) return "local_only";
        var levels = new List<string>();
        if (metadata.AccessOverride is not null) levels.Add(metadata.AccessOverride);
        else if (metadata.SchemaVersion != "3") levels.Add(metadata.Access);
        foreach (var reference in metadata.SourceRefs)
        {
            try { levels.Add(Show(reference.SourceId).Record.Access); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return "local_only"; }
        }
        if (levels.Any(x => x is not ("local_only" or "unclassified" or "external_allowed"))) return "local_only";
        if (levels.Contains("local_only")) return "local_only";
        if (levels.Contains("unclassified")) return "unclassified";
        return "external_allowed";
    }
    public (SourceRecord Record, string Text) ReadExtractedSource(Guid id)
    {
        var (record, directory) = Show(id); var path = Path.Combine(directory, "extracted.txt");
        if (!File.Exists(path) || record.Extractor.Status != "indexed") throw new FileNotFoundException("Source text unavailable");
        var text = NormalizeLf(File.ReadAllText(path, Encoding.UTF8));
        if (record.ExtractedTextSha256 is null || !string.Equals(HashText(text), record.ExtractedTextSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Source text hash mismatch");
        return (record, text);
    }
    public KnowledgeRecordView CreateAgentDraft(Guid sourceId, string title, string body, int startLine, int endLine, string quote)
        => DraftCandidate(sourceId, title, body, startLine, endLine, quote);
    public KnowledgeRecordView CreateAgentDraft(string title, string body, IReadOnlyList<(Guid SourceId, int StartLine, int EndLine, string Quote)> citations)
        => knowledge.DraftAgent(title, body, citations, index.IndexKnowledge);
    private string SafeMigrationPath(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("migration journal contains an absolute path");
        var path = Path.GetFullPath(Path.Combine(root, relative)); var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("migration journal path escapes database root");
        for (var current = path; current.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); current = Path.GetDirectoryName(current)!)
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("migration journal path traverses a symbolic link");
        return path;
    }

    public IReadOnlyList<KnowledgeRecordView> ListRecords(string? status = null) => knowledge.List(status);
    public KnowledgeRecordView ShowRecord(Guid id) => knowledge.Show(id);
    public IReadOnlyList<object> ShowRecordSources(Guid id) => knowledge.ShowSources(id);
    public IReadOnlyList<object> ShowRecordRelations(Guid id)
    {
        var records = knowledge.ListCanonical(); var target = records.SingleOrDefault(x => x.Metadata.Id == id) ?? throw new FileNotFoundException($"Knowledge record {id} was not found");
        var relations = (target.Metadata.RelatedRecords ?? Array.Empty<RelatedRecord>()).Select(x => new { type = x.Type, record_id = x.RecordId, direction = "outgoing" })
            .Concat(records.Where(x => x.Metadata.Id != id).SelectMany(x => (x.Metadata.RelatedRecords ?? Array.Empty<RelatedRecord>())
                .Where(r => r.Type == "conflicts_with" && r.RecordId == id).Select(_ => new { type = "conflicts_with", record_id = x.Metadata.Id, direction = "incoming" })))
            .Select(x => (object)x).ToArray();
        return relations;
    }
    public IReadOnlyList<KnowledgeSearchResult> SearchKnowledge(string query, int limit = 10, string? recordStatus = "reviewed")
    {
        var candidates = index.SearchKnowledge(query, Math.Clamp(limit * 5, 1, 100), recordStatus == "stale" ? null : recordStatus);
        var live = knowledge.List().ToDictionary(x => x.Metadata.Id);
        return candidates.Where(x => live.TryGetValue(x.Id, out var current) && (recordStatus is null || current.Metadata.Status == recordStatus))
            .Select(x => live.TryGetValue(x.Id, out var current) ? x with { Status = current.Metadata.Status } : x).Take(Math.Clamp(limit, 1, 100)).ToArray();
    }
    public KnowledgeRecordView DraftRecord(Guid sourceId, string title, string body, string type = "fact", string kind = "source_claim") => knowledge.Draft(sourceId, title, body, type, kind);
    public KnowledgeRecordView DraftCandidate(Guid sourceId, string title, string body, int startLine, int endLine, string quote, string type = "fact", string kind = "source_claim", string? operationId = null, string? adapterVersion = null, string? modelVersion = null)
    {
        return knowledge.DraftCandidate(sourceId, title, body, startLine, endLine, quote, type, kind, operationId, adapterVersion, modelVersion, index.IndexKnowledge);
    }
    public KnowledgeRecordView EditRecord(Guid id, string markdown, string? expectedFileHash = null)
    {
        return knowledge.Edit(id, markdown, expectedFileHash, index.IndexKnowledge);
    }
    public KnowledgeRecordView AcceptRecord(Guid id, IReadOnlyCollection<Guid> confirmedSources)
    {
        return knowledge.Accept(id, confirmedSources, index.IndexKnowledge);
    }
    public KnowledgeRecordView RejectRecord(Guid id, string reason)
    {
        return knowledge.Reject(id, reason, index.IndexKnowledge);
    }
}
