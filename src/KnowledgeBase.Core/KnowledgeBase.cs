using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;

namespace KnowledgeBase.Core;

public sealed record ExtractorInfo(string Name, string Version, string Status, string? Error = null);
public sealed record SourceRecord(string SchemaVersion, Guid SourceId, string ContentSha256, string OriginalName,
    string SourceType, string? MimeType, DateTimeOffset ImportedAt, DateTimeOffset LastSeenAt, string? OriginPath,
    ExtractorInfo Extractor, string Access, string[] Aliases);
public sealed record ImportSummary(int Imported, int Duplicates, int Indexed, int Unsupported, int Errors, int Oversized);
public sealed record SearchResult(Guid SourceId, string OriginalName, string SourceType, DateTimeOffset ImportedAt,
    string SourcePath, string Snippet, string Status);

public sealed class KnowledgeBaseStore
{
    private const long DefaultMaxFileBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string root;
    private string Sources => Path.Combine(root, "sources");
    private string Database => Path.Combine(root, "index", "search.sqlite");

    public KnowledgeBaseStore(string path) => root = Path.GetFullPath(path);

    public void Initialize()
    {
        Directory.CreateDirectory(Sources);
        Directory.CreateDirectory(Path.Combine(root, "index"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var config = Path.Combine(root, "config", "settings.json");
        if (!File.Exists(config)) File.WriteAllText(config, "{\"schema_version\":\"1\",\"max_file_size_bytes\":52428800}\n");
        EnsureIndex();
    }

    public ImportSummary Import(string inputPath)
    {
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath)) throw new FileNotFoundException("Input path does not exist", inputPath);
        Initialize();
        var files = File.Exists(inputPath) ? new[] { Path.GetFullPath(inputPath) } : EnumerateFiles(Path.GetFullPath(inputPath)).ToArray();
        var map = ReadSources().GroupBy(x => x.Record.ContentSha256, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        int imported = 0, duplicates = 0, indexed = 0, unsupported = 0, errors = 0, oversized = 0;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > DefaultMaxFileBytes) { oversized++; continue; }
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
                var origin = File.Exists(inputPath) ? file : Path.GetRelativePath(Path.GetFullPath(inputPath), file);
                if (map.TryGetValue(hash, out var existing))
                {
                    var aliases = existing.Record.Aliases.Append(origin).Distinct(StringComparer.Ordinal).ToArray();
                    var updated = existing.Record with { LastSeenAt = DateTimeOffset.UtcNow, Aliases = aliases };
                    SaveRecord(existing.Directory, updated); duplicates++;
                    continue;
                }
                var id = Guid.NewGuid(); var temp = Path.Combine(Sources, ".tmp-" + id.ToString("N"));
                Directory.CreateDirectory(temp);
                var ext = Path.GetExtension(file); if (string.IsNullOrWhiteSpace(ext) || ext.Length > 20 || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ext = ".bin";
                File.Copy(file, Path.Combine(temp, "original" + ext), false);
                var extraction = Extract(file);
                if (extraction.Text is not null) File.WriteAllText(Path.Combine(temp, "extracted.txt"), extraction.Text, new UTF8Encoding(false));
                var now = DateTimeOffset.UtcNow;
                var record = new SourceRecord("1", id, hash, Path.GetFileName(file), "file", Mime(file), now, now, origin,
                    new ExtractorInfo(extraction.Name, "1", extraction.Status, extraction.Error), "unclassified", Array.Empty<string>());
                SaveRecord(temp, record);
                var final = Path.Combine(Sources, id.ToString()); Directory.Move(temp, final); imported++;
                if (record.Extractor.Status == "indexed") indexed++;
                if (record.Extractor.Status == "unsupported") unsupported++;
                if (record.Extractor.Status == "extraction_error") errors++;
                map[hash] = (record, final);
                Index(record, extraction.Text ?? string.Empty);
            }
            catch { errors++; }
        }
        return new ImportSummary(imported, duplicates, indexed, unsupported, errors, oversized);
    }

    public IReadOnlyList<SearchResult> Search(string query, int limit = 10, string? sourceType = null, DateTimeOffset? from = null, DateTimeOffset? to = null, string? status = null)
    {
        EnsureIndex();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT s.source_id,s.original_name,s.source_type,s.imported_at,s.source_path,s.status,snippet(search,3,'[',']','…',18) FROM search JOIN sources s ON s.source_id=search.source_id WHERE search MATCH $q AND ($type IS NULL OR s.source_type=$type) AND ($from IS NULL OR s.imported_at >= $from) AND ($to IS NULL OR s.imported_at <= $to) AND ($status IS NULL OR s.status=$status) ORDER BY bm25(search),s.imported_at DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", query); cmd.Parameters.AddWithValue("$type", (object?)sourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)from?.ToString("O") ?? DBNull.Value); cmd.Parameters.AddWithValue("$to", (object?)to?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var results = new List<SearchResult>(); using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(6), reader.GetString(5)));
        return results;
    }

    public (SourceRecord Record, string Directory) Show(Guid id)
    {
        var dir = Path.Combine(Sources, id.ToString()); var path = Path.Combine(dir, "source.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Source {id} was not found");
        return (JsonSerializer.Deserialize<SourceRecord>(File.ReadAllText(path), JsonOptions)!, dir);
    }

    public object Status()
    {
        var records = ReadSources().Select(x => x.Record).ToArray();
        return new { schema_version = "1", index_exists = File.Exists(Database), total = records.Length, statuses = records.GroupBy(x => x.Extractor.Status).ToDictionary(g => g.Key, g => g.Count()), recent_errors = records.Where(x => x.Extractor.Error is not null).TakeLast(10).Select(x => new { x.SourceId, x.OriginalName, x.Extractor.Error }) };
    }

    public (int Indexed, int Skipped) Rebuild()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!); if (File.Exists(Database)) File.Delete(Database); EnsureIndex();
        int indexed = 0, skipped = 0;
        foreach (var (record, dir) in ReadSources())
        {
            var textPath = Path.Combine(dir, "extracted.txt"); var text = File.Exists(textPath) ? File.ReadAllText(textPath) : string.Empty;
            if (record.Extractor.Status == "indexed" && text.Length > 0) { Index(record, text); indexed++; } else { Index(record, string.Empty); skipped++; }
        }
        return (indexed, skipped);
    }

    private IEnumerable<string> EnumerateFiles(string dir)
    {
        var ignoreFile = Path.Combine(dir, ".kbignore"); var patterns = File.Exists(ignoreFile) ? File.ReadAllLines(ignoreFile).Where(x => !string.IsNullOrWhiteSpace(x) && !x.TrimStart().StartsWith('#')).Select(x => x.Trim()).ToArray() : Array.Empty<string>();
        foreach (var file in Directory.EnumerateFiles(dir)) if (Path.GetFileName(file) != ".kbignore") yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (new[] { ".git", ".vs", "bin", "obj", "node_modules", "packages" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (patterns.Any(p => name.Contains(p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var file in EnumerateFiles(sub)) yield return file;
        }
    }

    private static (string? Text, string Name, string Status, string? Error) Extract(string file)
    {
        try
        {
            if (Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                using var pdf = PdfDocument.Open(file); var text = string.Join("\n\n", pdf.GetPages().Select(p => p.Text)).Trim();
                return text.Length == 0 ? ("", "pdfpig", "empty_text", null) : (text, "pdfpig", "indexed", null);
            }
            var bytes = File.ReadAllBytes(file);
            if (bytes.Contains((byte)0)) return (null, "none", "unsupported", null);
            var decoded = new UTF8Encoding(false, true).GetString(bytes);
            return string.IsNullOrWhiteSpace(decoded) ? ("", "utf8", "empty_text", null) : (decoded, "utf8", "indexed", null);
        }
        catch (DecoderFallbackException) { return (null, "none", "unsupported", null); }
        catch (Exception ex) { return (null, Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "pdfpig" : "utf8", "extraction_error", ex.Message); }
    }

    private IEnumerable<(SourceRecord Record, string Directory)> ReadSources()
    {
        if (!Directory.Exists(Sources)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(Sources))
        {
            var path = Path.Combine(dir, "source.json"); if (!File.Exists(path)) continue;
            SourceRecord? record = null; try { record = JsonSerializer.Deserialize<SourceRecord>(File.ReadAllText(path), JsonOptions); } catch { }
            if (record is not null) yield return (record, dir);
        }
    }
    private static void SaveRecord(string dir, SourceRecord record) => File.WriteAllText(Path.Combine(dir, "source.json"), JsonSerializer.Serialize(record, JsonOptions) + "\n");
    private static string? Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch { ".txt" => "text/plain", ".md" => "text/markdown", ".html" or ".htm" => "text/html", ".json" => "application/json", ".csv" => "text/csv", ".pdf" => "application/pdf", _ => null };
    private SqliteConnection Open() { var c = new SqliteConnection($"Data Source={Database}"); c.Open(); return c; }
    private void EnsureIndex()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Database)!); using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS sources(source_id TEXT PRIMARY KEY,original_name TEXT,source_type TEXT,imported_at TEXT,source_path TEXT,status TEXT); CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(source_id UNINDEXED,original_name,source_type,body,tokenize='unicode61');"; cmd.ExecuteNonQuery();
    }
    private void Index(SourceRecord r, string body)
    {
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO sources VALUES($id,$name,$type,$date,$path,$status); DELETE FROM search WHERE source_id=$id; INSERT INTO search(source_id,original_name,source_type,body) VALUES($id,$name,$type,$body)";
        cmd.Parameters.AddWithValue("$id", r.SourceId.ToString()); cmd.Parameters.AddWithValue("$name", r.OriginalName); cmd.Parameters.AddWithValue("$type", r.SourceType); cmd.Parameters.AddWithValue("$date", r.ImportedAt.ToString("O")); cmd.Parameters.AddWithValue("$path", Path.Combine("sources", r.SourceId.ToString(), "original" + Path.GetExtension(r.OriginalName))); cmd.Parameters.AddWithValue("$status", r.Extractor.Status); cmd.Parameters.AddWithValue("$body", body); cmd.ExecuteNonQuery(); tx.Commit();
    }
}
