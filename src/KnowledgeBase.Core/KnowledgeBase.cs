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
    ExtractorInfo Extractor, string Access, string[] Aliases, string? OriginalRelativePath = null);
public sealed record ImportItem(string Path, string Status, Guid? SourceId = null, string? Message = null, string? Details = null);
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
                if (record.SchemaVersion != "1" || record.SourceId == Guid.Empty ||
                    !string.Equals(Path.GetFileName(dir), record.SourceId.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(record.ContentSha256 ?? "", "^[0-9a-fA-F]{64}$") || record.Extractor is null ||
                    string.IsNullOrWhiteSpace(record.Extractor.Status) || record.Aliases is null ||
                    string.IsNullOrWhiteSpace(record.OriginalName) || record.ImportedAt == default || record.LastSeenAt == default)
                    throw new InvalidDataException("source.json contains invalid schema or identity fields");
                var utcImportedAt = record.ImportedAt.ToUniversalTime(); var utcLastSeenAt = record.LastSeenAt.ToUniversalTime();
                if (utcImportedAt != record.ImportedAt || utcLastSeenAt != record.LastSeenAt)
                {
                    record = record with { ImportedAt = utcImportedAt, LastSeenAt = utcLastSeenAt };
                    SaveRecord(dir, record);
                }
                var original = record.OriginalRelativePath;
                if (string.IsNullOrWhiteSpace(original))
                {
                    var actual = Directory.EnumerateFiles(dir, "original.*").FirstOrDefault()
                        ?? Directory.EnumerateFiles(dir, "original").FirstOrDefault();
                    if (actual is not null)
                    {
                        original = Path.GetRelativePath(dir, actual);
                        record = record with { OriginalRelativePath = original };
                        SaveRecord(dir, record);
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
        cmd.CommandText = "CREATE TABLE sources(source_id TEXT PRIMARY KEY,original_name TEXT,source_type TEXT,imported_at TEXT,source_path TEXT,status TEXT); CREATE VIRTUAL TABLE search USING fts5(source_id UNINDEXED,original_name,source_type,body,tokenize='unicode61');";
        cmd.ExecuteNonQuery();
    }
    public void Ensure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        if (File.Exists(database)) return;
        using var c = Open(); CreateSchema(c);
    }
    public void Index(SourceRecord r, string body)
    {
        Ensure(); using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO sources VALUES($id,$name,$type,$date,$path,$status); DELETE FROM search WHERE source_id=$id; INSERT INTO search(source_id,original_name,source_type,body) VALUES($id,$name,$type,$body)";
        Bind(cmd, r, body); cmd.ExecuteNonQuery(); tx.Commit();
    }
    private static void Bind(SqliteCommand cmd, SourceRecord r, string body)
    {
        cmd.Parameters.AddWithValue("$id", r.SourceId.ToString()); cmd.Parameters.AddWithValue("$name", r.OriginalName);
        cmd.Parameters.AddWithValue("$type", r.SourceType); cmd.Parameters.AddWithValue("$date", r.ImportedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$path", $"sources/{r.SourceId}/{(r.OriginalRelativePath ?? "original.bin").Replace('\\', '/')}");
        cmd.Parameters.AddWithValue("$status", r.Extractor.Status); cmd.Parameters.AddWithValue("$body", body);
    }
    public (int Indexed, int Skipped) Rebuild(IEnumerable<(SourceRecord Record, string Directory)> sources, int expected)
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
                    var textPath = Path.Combine(dir, "extracted.txt"); var text = File.Exists(textPath) ? File.ReadAllText(textPath) : "";
                    if (record.Extractor.Status == "indexed" && text.Length > 0) indexed++; else skipped++;
                    using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO sources VALUES($id,$name,$type,$date,$path,$status); INSERT INTO search(source_id,original_name,source_type,body) VALUES($id,$name,$type,$body)";
                    Bind(cmd, record, text); cmd.ExecuteNonQuery(); tx.Commit();
                }
                using var check = c.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
                if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SQLite integrity_check failed");
                using var count = c.CreateCommand(); count.CommandText = "SELECT count(*) FROM sources";
                if (Convert.ToInt32(count.ExecuteScalar()) != expected) throw new InvalidDataException("Rebuilt index source count does not match valid source metadata");
            }
            File.Move(temp, database, true); return (indexed, skipped);
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
            var rows = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            using (var reader = cmd.ExecuteReader()) while (reader.Read()) rows[reader.GetString(0)] = Enumerable.Range(1, 5).Select(reader.GetString).ToArray();
            var expected = records.ToDictionary(r => r.SourceId.ToString(), StringComparer.OrdinalIgnoreCase);
            using var ftsCommand = c.CreateCommand(); ftsCommand.CommandText = "SELECT source_id,original_name,source_type FROM search";
            var ftsRows = new Dictionary<string, (string Name, string Type)>(StringComparer.OrdinalIgnoreCase);
            using (var reader = ftsCommand.ExecuteReader()) while (reader.Read()) ftsRows[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
            var allIndexed = rows.Keys.Union(ftsRows.Keys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = expected.Count(pair => !rows.TryGetValue(pair.Key, out var row) || !ftsRows.TryGetValue(pair.Key, out var fts) ||
                !RowMatches(pair.Value, row) || fts.Name != pair.Value.OriginalName || fts.Type != pair.Value.SourceType);
            return (integrity, missing, allIndexed.Except(expected.Keys, StringComparer.OrdinalIgnoreCase).Count(), rows.Count);
        }
        catch (Exception ex) { return ($"invalid: {ex.Message}", records.Count, 0, 0); }

        static bool RowMatches(SourceRecord record, string[] row) =>
            row[0] == record.OriginalName && row[1] == record.SourceType &&
            row[2] == record.ImportedAt.ToUniversalTime().ToString("O") &&
            row[3] == $"sources/{record.SourceId}/{(record.OriginalRelativePath ?? "original.bin").Replace('\\', '/')}" &&
            row[4] == record.Extractor.Status;
    }
    public IReadOnlyList<SearchResult> Search(string query, int limit, string? sourceType, DateTimeOffset? from, DateTimeOffset? to, string? status)
    {
        Ensure(); using var connection = Open(); using var cmd = connection.CreateCommand();
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
    (int Indexed, int Skipped) Rebuild(IEnumerable<(SourceRecord Record, string Directory)> sources, int expected);
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
    private string SettingsPath => Path.Combine(root, "config", "settings.json");
    public KnowledgeBaseStore(string path, IEnumerable<ITextExtractor>? textExtractors = null, ISearchIndex? searchIndex = null)
    {
        root = Path.GetFullPath(path); repository = new(root); index = searchIndex ?? new SearchIndex(root);
        extractors = (textExtractors ?? new ITextExtractor[] { new PdfTextExtractor(), new Utf8TextExtractor() }).ToArray();
    }
    public void Initialize()
    {
        Directory.CreateDirectory(repository.SourcesPath); Directory.CreateDirectory(Path.Combine(root, "index")); Directory.CreateDirectory(Path.Combine(root, "config"));
        if (!File.Exists(SettingsPath)) File.WriteAllText(SettingsPath, "{\"schema_version\":\"1\",\"max_file_size_bytes\":52428800}\n");
        index.Ensure();
    }
    private long ReadMaxFileSize()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath)); var obj = doc.RootElement;
        if (obj.ValueKind != JsonValueKind.Object) throw new InvalidDataException("settings.json must contain a JSON object");
        if (!obj.TryGetProperty("schema_version", out var version) || version.GetString() != "1") throw new InvalidDataException("settings.json has an unsupported schema_version");
        if (!obj.TryGetProperty("max_file_size_bytes", out var size) || !size.TryGetInt64(out var value) || value <= 0 || value > MaxSupportedFileBytes)
            throw new InvalidDataException($"settings.json max_file_size_bytes must be between 1 and {MaxSupportedFileBytes}");
        return value;
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
        using var operation = AcquireLock(); Initialize(); var maxSize = ReadMaxFileSize(); var fullInput = Path.GetFullPath(inputPath);
        var skippedLinks = 0; var items = new List<ImportItem>();
        string[] files;
        if (File.Exists(fullInput) && (File.GetAttributes(fullInput) & FileAttributes.ReparsePoint) != 0) { files = Array.Empty<string>(); skippedLinks = 1; }
        else if (File.Exists(fullInput)) files = new[] { fullInput };
        else files = EnumerateFiles(fullInput, out skippedLinks, items).ToArray();
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
                    if (extraction.Text is not null) File.WriteAllText(Path.Combine(temp, "extracted.txt"), extraction.Text, new UTF8Encoding(false));
                    var now = DateTimeOffset.UtcNow; var record = new SourceRecord("1", id, copiedHash, Path.GetFileName(file), "file", Mime(file), now, now, relative,
                        new(extraction.Name, "1", extraction.Status, extraction.Status == "extraction_error" ? (extraction.Name == "pdfpig" ? "PDF could not be read" : "Text could not be read") : extraction.Error),
                        "unclassified", Array.Empty<string>(), originalRelative);
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
            recent_errors = records.Where(x => x.Extractor.Error is not null).TakeLast(10).Select(x => new { x.SourceId, x.OriginalName, x.Extractor.Error })
        };
    }
    public (int Indexed, int Skipped) Rebuild()
    {
        using var operation = AcquireLock(); Initialize(); var problems = new List<string>(); var records = repository.ReadSources(problems).ToArray();
        if (problems.Count > 0) throw new InvalidDataException("Cannot rebuild index because source metadata is invalid: " + string.Join("; ", problems));
        return index.Rebuild(records, records.Length);
    }
}
