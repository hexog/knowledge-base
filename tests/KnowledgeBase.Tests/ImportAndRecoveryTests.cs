using KnowledgeBase.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace KnowledgeBase.Tests;

public sealed class ImportAndRecoveryTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "kb-tests-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(temp, "db");
    public ImportAndRecoveryTests() => Directory.CreateDirectory(temp);
    public void Dispose() { if (Directory.Exists(temp)) Directory.Delete(temp, true); }

    [Fact]
    public void ImportsUtf8DeduplicatesAndStoresActualOriginalPath()
    {
        var input = Path.Combine(temp, "input"); Directory.CreateDirectory(input);
        var path = Path.Combine(input, "meeting"); File.WriteAllText(path, "Project kestrel notes", new System.Text.UTF8Encoding(false));
        var store = new KnowledgeBaseStore(Db);
        var first = store.Import(path); var record = store.Show(first.Items.Single().SourceId!.Value);
        Assert.Equal("indexed", first.Items.Single().Status);
        Assert.True(File.Exists(Path.Combine(record.Directory, record.Record.OriginalRelativePath!)));
        var copy = Path.Combine(input, "copy.md"); File.WriteAllText(copy, "Project kestrel notes", new System.Text.UTF8Encoding(false));
        var again = store.Import(copy);
        Assert.Equal(1, again.Duplicates);
        Assert.Equal(record.Record.SourceId, again.Items.Single().SourceId);
        Assert.Contains("copy.md", store.Show(record.Record.SourceId).Record.Aliases);
        var found = Assert.Single(store.Search("kestrel"));
        Assert.Equal($"sources/{record.Record.SourceId}/original.bin", found.SourcePath);
    }

    [Fact]
    public void ContinuesAfterErrorsAndHonorsSettingsAndIgnoreGlobs()
    {
        var input = Path.Combine(temp, "input"); Directory.CreateDirectory(Path.Combine(input, "private"));
        File.WriteAllText(Path.Combine(input, "ok.txt"), "public searchable text");
        File.WriteAllText(Path.Combine(input, "private", "secret.txt"), "secret searchable text");
        File.WriteAllBytes(Path.Combine(input, "binary.dat"), new byte[] { 0, 1, 2 });
        File.WriteAllText(Path.Combine(input, "large.txt"), new string('x', 40));
        File.WriteAllText(Path.Combine(input, "hidden.skip"), "should not import");
        File.WriteAllText(Path.Combine(input, ".kbignore"), "private/**\n*.skip");
        var store = new KnowledgeBaseStore(Db); store.Initialize();
        File.WriteAllText(Path.Combine(Db, "config", "settings.json"), "{\"schema_version\":\"1\",\"max_file_size_bytes\":30}");
        var summary = store.Import(input);
        Assert.Equal(2, summary.Imported);
        Assert.Equal(1, summary.Oversized);
        Assert.DoesNotContain(summary.Items, x => x.Path.StartsWith("private", StringComparison.Ordinal));
        Assert.DoesNotContain(summary.Items, x => x.Path == "hidden.skip");
        Assert.Equal("unsupported", summary.Items.Single(x => x.Path == "binary.dat").Status);
        Assert.Single(store.Search("public"));
    }

    [Fact]
    public void InvalidSettingsAreReported()
    {
        var store = new KnowledgeBaseStore(Db); store.Initialize();
        File.WriteAllText(Path.Combine(Db, "config", "settings.json"), "{\"schema_version\":\"1\",\"max_file_size_bytes\":0}");
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "text");
        Assert.Throws<InvalidDataException>(() => store.Import(input));
    }

    [Fact]
    public void EmptyAndInvalidPdfAreReportedWithoutStoppingImport()
    {
        var input = Path.Combine(temp, "input"); Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "empty.txt"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(input, "broken.pdf"), "not a pdf");
        var summary = new KnowledgeBaseStore(Db).Import(input);
        Assert.Equal(2, summary.Imported);
        Assert.Equal("empty_text", summary.Items.Single(x => x.Path == "empty.txt").Status);
        Assert.Equal("extraction_error", summary.Items.Single(x => x.Path == "broken.pdf").Status);
        Assert.Equal(1, summary.Errors);
    }

    [Fact]
    public void ExtractsTextFromPdfTextLayer()
    {
        var input = Path.Combine(temp, "searchable.pdf"); File.WriteAllBytes(input, CreatePdf("PDF falcon passage"));
        var store = new KnowledgeBaseStore(Db); var summary = store.Import(input);
        Assert.Equal("indexed", Assert.Single(summary.Items).Status);
        Assert.Single(store.Search("falcon"));
    }

    [Fact]
    public void CliUsesJsonContractAndReportsArgumentErrorsAsJson()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "cli integration word");
        var imported = RunCli("--json", "--db", Db, "import", input);
        Assert.Equal(0, imported.ExitCode);
        using (var json = JsonDocument.Parse(imported.Stdout))
        {
            Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("imported").GetInt32());
            Assert.True(json.RootElement.GetProperty("items")[0].TryGetProperty("source_id", out _));
        }
        var todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var dateBounded = RunCli("--json", "--db", Db, "search", "integration", "--to", todayUtc);
        Assert.Single(JsonDocument.Parse(dateBounded.Stdout).RootElement.EnumerateArray());
        var invalid = RunCli("--json", "import");
        Assert.Equal(2, invalid.ExitCode);
        Assert.Equal("argument_error", JsonDocument.Parse(invalid.Stdout).RootElement.GetProperty("error").GetString());
        var brokenPdf = Path.Combine(temp, "broken.pdf"); File.WriteAllText(brokenPdf, "invalid pdf");
        var safeError = RunCli("--json", "--db", Path.Combine(temp, "safe-db"), "import", brokenPdf);
        Assert.Equal(1, safeError.ExitCode);
        Assert.False(JsonDocument.Parse(safeError.Stdout).RootElement.GetProperty("items")[0].TryGetProperty("details", out _));
        var verboseError = RunCli("--json", "--verbose", "--db", Path.Combine(temp, "verbose-db"), "import", brokenPdf);
        Assert.True(JsonDocument.Parse(verboseError.Stdout).RootElement.GetProperty("items")[0].TryGetProperty("details", out _));
        var human = RunCli("--db", Db, "status");
        Assert.DoesNotContain("{", human.Stdout);
        Assert.Contains("total:", human.Stdout);
    }

    [Fact]
    public void RebuildFailureKeepsWorkingIndexAndSuccessfulRebuildRecovers()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "recoverable falcon document");
        var store = new KnowledgeBaseStore(Db); var result = store.Import(input); var record = store.Show(result.Items.Single().SourceId!.Value);
        var metadata = Path.Combine(record.Directory, "source.json"); var saved = File.ReadAllText(metadata);
        File.WriteAllText(metadata, "broken json");
        Assert.Throws<InvalidDataException>(() => store.Rebuild());
        Assert.Single(store.Search("falcon"));
        File.WriteAllText(metadata, saved);
        Assert.Equal(1, store.Rebuild().Indexed);
        Assert.Equal(0, store.Rebuild().Skipped);
        Assert.Single(store.Search("falcon"));
    }

    [Fact]
    public void FailedIndexWriteKeepsSourceAndCanBeRecovered()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "durable index recovery");
        var store = new KnowledgeBaseStore(Db, searchIndex: new FailingIndex(new SearchIndex(Db)));
        var result = store.Import(input);
        Assert.Equal("index_error", Assert.Single(result.Items).Status);
        Assert.Equal(1, result.Errors);
        Assert.Equal(1, JsonDocument.Parse(JsonSerializer.Serialize(store.Status(true))).RootElement.GetProperty("index_missing_sources").GetInt32());
        var recovered = new KnowledgeBaseStore(Db);
        Assert.Equal(1, recovered.Rebuild().Indexed);
        Assert.Single(recovered.Search("durable"));
    }

    [Fact]
    public void VerifyFindsIndexEntriesThatDoNotMatchSources()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "verify consistency");
        var store = new KnowledgeBaseStore(Db); store.Import(input);
        var db = Path.Combine(Db, "index", "search.sqlite");
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM sources"; command.ExecuteNonQuery();
        }
        var status = store.Status(true);
        Assert.Contains("\"index_missing_sources\":1", JsonSerializer.Serialize(status));
    }

    [Fact]
    public void SkipsSymlinksWhenWalkingDirectories()
    {
        var input = Path.Combine(temp, "input"); Directory.CreateDirectory(input); var outside = Path.Combine(temp, "outside.txt"); File.WriteAllText(outside, "outside document");
        try { File.CreateSymbolicLink(Path.Combine(input, "linked.txt"), outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }
        var summary = new KnowledgeBaseStore(Db).Import(input);
        Assert.Equal(1, summary.SkippedSymlinks);
        Assert.Empty(summary.Items);
    }

    private (int ExitCode, string Stdout, string Stderr) RunCli(params string[] arguments)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "KnowledgeBase.Cli.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(assembly); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    private static byte[] CreatePdf(string text)
    {
        var stream = $"BT /F1 18 Tf 20 90 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 144] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream"
        };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString()); pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private sealed class FailingIndex(ISearchIndex inner) : ISearchIndex
    {
        public string DatabasePath => inner.DatabasePath;
        public void Ensure() => inner.Ensure();
        public void Index(SourceRecord record, string body) => throw new IOException("injected index failure");
        public (int Indexed, int Skipped) Rebuild(IEnumerable<(SourceRecord Record, string Directory)> sources, int expected) => inner.Rebuild(sources, expected);
        public (string Integrity, int Missing, int Extra, int Count) Verify(IReadOnlyCollection<SourceRecord> records) => inner.Verify(records);
        public IReadOnlyList<SearchResult> Search(string query, int limit, string? sourceType, DateTimeOffset? from, DateTimeOffset? to, string? status) => inner.Search(query, limit, sourceType, from, to, status);
    }
}
