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
        var rebuilt = RunCli("--json", "--db", Db, "rebuild-index");
        Assert.Equal(0, rebuilt.ExitCode);
        Assert.True(JsonDocument.Parse(rebuilt.Stdout).RootElement.TryGetProperty("indexed", out _));
    }

    [Fact]
    public void CliCreatesShowsAcceptsAndSearchesCitationBackedRecord()
    {
        var input = Path.Combine(temp, "cli-evidence.txt"); File.WriteAllText(input, "Exact CLI citation.\nAdditional context.");
        var imported = RunCli("--json", "--db", Db, "import", input);
        var sourceId = JsonDocument.Parse(imported.Stdout).RootElement.GetProperty("items")[0].GetProperty("source_id").GetGuid();
        var draft = RunCli("--json", "--db", Db, "record", "draft", "--source", sourceId.ToString(), "--title", "CLI knowledge claim", "--body", "The claim has evidence.", "--start-line", "1", "--end-line", "1", "--quote", "Exact CLI citation.");
        Assert.Equal(0, draft.ExitCode);
        var recordId = JsonDocument.Parse(draft.Stdout).RootElement.GetProperty("record").GetProperty("metadata").GetProperty("id").GetGuid();
        var shown = RunCli("--json", "--db", Db, "record", "show", recordId.ToString(), "--sources");
        Assert.Equal("Exact CLI citation.", JsonDocument.Parse(shown.Stdout).RootElement.GetProperty("sources")[0].GetProperty("quote").GetString());
        var accepted = RunCli("--json", "--db", Db, "record", "accept", recordId.ToString(), "--confirm-source", sourceId.ToString());
        Assert.Equal("reviewed", JsonDocument.Parse(accepted.Stdout).RootElement.GetProperty("metadata").GetProperty("status").GetString());
        var results = RunCli("--json", "--db", Db, "search", "knowledge", "--scope", "knowledge", "--record-status", "reviewed");
        Assert.Equal(recordId, JsonDocument.Parse(results.Stdout).RootElement[0].GetProperty("id").GetGuid());
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
        using (var connection = new SqliteConnection($"Data Source={db};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM sources"; command.ExecuteNonQuery();
        }
        var status = store.Status(true);
        Assert.Contains("\"index_missing_sources\":1", JsonSerializer.Serialize(status));
    }

    [Fact]
    public void VerifyDetectsOriginalAndExtractedTextTampering()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "integrity evidence");
        var store = new KnowledgeBaseStore(Db); var id = store.Import(input).Items.Single().SourceId!.Value; var source = store.Show(id);
        File.WriteAllText(Path.Combine(source.Directory, "original.txt"), "tampered original");
        File.WriteAllText(Path.Combine(source.Directory, "extracted.txt"), "tampered evidence");
        var status = JsonDocument.Parse(JsonSerializer.Serialize(store.Status(true))).RootElement;
        Assert.False(status.GetProperty("verify_ok").GetBoolean());
        var errors = status.GetProperty("verify_errors").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(errors, x => x!.Contains($"{id}: original_hash_mismatch", StringComparison.Ordinal));
        Assert.Contains(errors, x => x!.Contains($"{id}: extracted_text_hash_mismatch", StringComparison.Ordinal));
        var cli = RunCli("--json", "--db", Db, "status", "--verify");
        Assert.Equal(1, cli.ExitCode);
    }

    [Fact]
    public void MissingIndexSearchRequiresRebuildAndRebuildRejectsMissingIndexedText()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "index recoverable text");
        var store = new KnowledgeBaseStore(Db); var id = store.Import(input).Items.Single().SourceId!.Value; var source = store.Show(id);
        File.Delete(Path.Combine(Db, "index", "search.sqlite"));
        Assert.Throws<InvalidDataException>(() => store.Search("recoverable"));
        store.Rebuild(); Assert.Single(store.Search("recoverable"));
        var before = File.ReadAllBytes(Path.Combine(Db, "index", "search.sqlite")); File.Delete(Path.Combine(source.Directory, "extracted.txt"));
        Assert.Throws<InvalidDataException>(() => store.Rebuild());
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Db, "index", "search.sqlite")));
    }

    [Fact]
    public void MigrationBacksUpMetadataAndRecordCanBeReviewedAndArchived()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "A verified statement.\nMore context.");
        var store = new KnowledgeBaseStore(Db); var id = store.Import(input).Items.Single().SourceId!.Value; var source = store.Show(id);
        var migration = JsonSerializer.Serialize(store.Migrate(2));
        Assert.Contains("migrated_sources", migration);
        Assert.True(File.Exists(Path.Combine(source.Directory, "source.json.v1.bak")));
        var draft = store.DraftCandidate(id, "Verified statement", "The source says this.", 1, 1, "A verified statement.");
        var accepted = store.AcceptRecord(draft.Metadata.Id, new[] { id });
        Assert.Equal("reviewed", accepted.Metadata.Status);
        var rejected = store.DraftCandidate(id, "Another statement", "Needs review.", 1, 1, "A verified statement.");
        var archived = store.RejectRecord(rejected.Metadata.Id, "Insufficient evidence");
        Assert.Equal("archived", archived.Metadata.Status);
        Assert.Equal("Insufficient evidence", archived.Metadata.RejectionReason);
        var metadataBeforeRepeat = File.ReadAllBytes(Path.Combine(source.Directory, "source.json"));
        store.Migrate(2);
        Assert.Equal(metadataBeforeRepeat, File.ReadAllBytes(Path.Combine(source.Directory, "source.json")));
    }

    [Fact]
    public void ExtractedTextLimitStoresOriginalWithoutPartialText()
    {
        var store = new KnowledgeBaseStore(Db); store.Initialize();
        File.WriteAllText(Path.Combine(Db, "config", "settings.json"), "{\"schema_version\":\"2\",\"max_file_size_bytes\":1000,\"max_extracted_text_bytes\":5}");
        var input = Path.Combine(temp, "large.txt"); File.WriteAllText(input, "more than five bytes");
        var item = Assert.Single(store.Import(input).Items); var source = store.Show(item.SourceId!.Value);
        Assert.Equal("text_limit_exceeded", item.Status);
        Assert.False(File.Exists(Path.Combine(source.Directory, "extracted.txt")));
        Assert.True(File.Exists(Path.Combine(source.Directory, source.Record.OriginalRelativePath!)));
    }

    [Fact]
    public void KnowledgeSearchDefaultsToReviewedAndRebuildIndexesPortableRecords()
    {
        var input = Path.Combine(temp, "input.txt"); File.WriteAllText(input, "A distinctive evidence sentence.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var draft = store.DraftCandidate(sourceId, "Distinctive reviewed claim", "The statement is distinctive.", 1, 1, "A distinctive evidence sentence.");
        Assert.Empty(store.SearchKnowledge("distinctive"));
        store.AcceptRecord(draft.Metadata.Id, new[] { sourceId });
        Assert.Single(store.SearchKnowledge("distinctive"));
        File.Delete(Path.Combine(Db, "index", "search.sqlite"));
        Assert.Throws<InvalidDataException>(() => store.SearchKnowledge("distinctive"));
        Assert.Equal(1, store.Rebuild().KnowledgeIndexed);
        Assert.Single(store.SearchKnowledge("distinctive"));
    }

    [Fact]
    public void ReviewedRecordBecomesStaleWhenExtractedEvidenceChanges()
    {
        var input = Path.Combine(temp, "source.txt"); File.WriteAllText(input, "Evidence remains unchanged.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var draft = store.DraftCandidate(sourceId, "A claim", "A supported claim.", 1, 1, "Evidence remains unchanged.");
        store.AcceptRecord(draft.Metadata.Id, new[] { sourceId });
        File.WriteAllText(Path.Combine(store.Show(sourceId).Directory, "extracted.txt"), "Evidence was externally changed.");
        var shown = store.ShowRecord(draft.Metadata.Id);
        Assert.Equal("stale", shown.Metadata.Status);
        Assert.Equal("source_changed_or_quote_mismatch", shown.StaleReason);
        Assert.Empty(store.SearchKnowledge("claim"));
        Assert.Single(store.SearchKnowledge("claim", 10, "stale"));
        File.WriteAllText(Path.Combine(store.Show(sourceId).Directory, "extracted.txt"), "Evidence remains unchanged.");
        var markdown = File.ReadAllText(shown.Path).Replace("A supported claim.", "An externally altered claim.", StringComparison.Ordinal);
        File.WriteAllText(shown.Path, markdown);
        var changedRecord = store.ShowRecord(draft.Metadata.Id);
        Assert.Equal("stale", changedRecord.Metadata.Status);
        Assert.Equal("record_content_changed", changedRecord.StaleReason);
        var status = JsonDocument.Parse(JsonSerializer.Serialize(store.Status(true))).RootElement;
        Assert.Contains(status.GetProperty("verify_errors").EnumerateArray().Select(x => x.GetString()), x => x!.Contains("stale (record_content_changed)", StringComparison.Ordinal));
    }

    [Fact]
    public void EditingReviewedRecordKeepsUnchangedReviewAndArchivesChangedVersion()
    {
        var input = Path.Combine(temp, "source.txt"); File.WriteAllText(input, "Exact citation.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var draft = store.DraftCandidate(sourceId, "Reviewed title", "Original body.", 1, 1, "Exact citation.");
        store.AcceptRecord(draft.Metadata.Id, new[] { sourceId });
        var reviewed = store.ShowRecord(draft.Metadata.Id); var unchanged = File.ReadAllText(reviewed.Path);
        Assert.Equal("reviewed", store.EditRecord(draft.Metadata.Id, unchanged, KnowledgeRepository.FileHash(unchanged)).Metadata.Status);
        var changed = unchanged.Replace("Original body.", "Edited body.", StringComparison.Ordinal);
        var edited = store.EditRecord(draft.Metadata.Id, changed, KnowledgeRepository.FileHash(unchanged));
        Assert.Equal("draft", edited.Metadata.Status);
        Assert.Null(edited.Metadata.ReviewedContentSha256);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(Db, "knowledge", "history", draft.Metadata.Id.ToString()), "*.md").Length);
    }

    [Fact]
    public void ConcurrentEditorSaveFailsWithoutReplacingTheNewVersion()
    {
        var input = Path.Combine(temp, "source.txt"); File.WriteAllText(input, "Exact citation.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var draft = store.DraftCandidate(sourceId, "Concurrent title", "Original body.", 1, 1, "Exact citation.");
        var original = File.ReadAllText(draft.Path); var changedInEditor = original.Replace("Original body.", "My editor copy.", StringComparison.Ordinal);
        File.WriteAllText(draft.Path, original.Replace("Original body.", "Other editor copy.", StringComparison.Ordinal));
        Assert.Throws<IOException>(() => store.EditRecord(draft.Metadata.Id, changedInEditor, KnowledgeRepository.FileHash(original)));
        Assert.Contains("Other editor copy.", File.ReadAllText(draft.Path));
    }

    [Fact]
    public void RecordValidationRejectsUnknownFieldsAndUncitedInference()
    {
        var input = Path.Combine(temp, "source.txt"); File.WriteAllText(input, "A verifiable sentence.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var draft = store.DraftCandidate(sourceId, "Validation claim", "A body.", 1, 1, "A verifiable sentence.");
        var markdown = File.ReadAllText(draft.Path);
        Assert.Throws<InvalidDataException>(() => store.EditRecord(draft.Metadata.Id, markdown.Replace("\n---\n", "\ncustom_field: silently_bad\n---\n", StringComparison.Ordinal)));
        var sourceRefsStart = markdown.IndexOf("source_refs:", StringComparison.Ordinal);
        var historyStart = markdown.IndexOf("\nhistory:", sourceRefsStart, StringComparison.Ordinal);
        var uncited = markdown[..sourceRefsStart] + "source_refs: []" + markdown[historyStart..];
        uncited = uncited.Replace("kind: source_claim", "kind: inference", StringComparison.Ordinal);
        store.EditRecord(draft.Metadata.Id, uncited);
        Assert.Throws<InvalidDataException>(() => store.AcceptRecord(draft.Metadata.Id, Array.Empty<Guid>()));
        Assert.Throws<InvalidDataException>(() => store.DraftCandidate(sourceId, "Bad quote", "Body.", 1, 1, "A different sentence."));
    }

    [Fact]
    public void FailedKnowledgeIndexingReturnsSavedIndexStaleAndRebuildRecovers()
    {
        var input = Path.Combine(temp, "source.txt"); File.WriteAllText(input, "Index recovery citation.");
        var baseStore = new KnowledgeBaseStore(Db); var sourceId = baseStore.Import(input).Items.Single().SourceId!.Value;
        var store = new KnowledgeBaseStore(Db, searchIndex: new FailingKnowledgeIndex(new SearchIndex(Db)));
        var draft = store.DraftCandidate(sourceId, "Index failure claim", "The body.", 1, 1, "Index recovery citation.");
        Assert.StartsWith("saved_index_stale:", draft.IndexStatus);
        Assert.Equal("draft", store.ShowRecord(draft.Metadata.Id).Metadata.Status);
        Assert.Equal(1, store.Rebuild().KnowledgeIndexed);
        Assert.Single(store.SearchKnowledge("failure", 10, "draft"));
    }

    [Fact]
    public void ConflictsAreVisibleFromBothRecordsAndMissingTargetsAreRejected()
    {
        var input = Path.Combine(temp, "source.txt"); File.WriteAllText(input, "Shared source evidence.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var earlier = store.DraftCandidate(sourceId, "Earlier claim", "Earlier body.", 1, 1, "Shared source evidence.");
        var newer = store.DraftCandidate(sourceId, "Newer claim", "Newer body.", 1, 1, "Shared source evidence.");
        var markdown = File.ReadAllText(newer.Path);
        var relation = $"related_records:\n- type: conflicts_with\n  record_id: {earlier.Metadata.Id}";
        var frontMatterEnd = markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        var yaml = markdown[4..frontMatterEnd];
        var replaced = System.Text.RegularExpressions.Regex.Replace(yaml, "(?ms)^related_records:.*?(?=^[A-Za-z_][A-Za-z0-9_]*:|\\z)", relation + "\n");
        if (replaced == yaml) replaced += "\n" + relation;
        markdown = "---\n" + replaced + markdown[frontMatterEnd..];
        store.EditRecord(newer.Metadata.Id, markdown);
        store.AcceptRecord(newer.Metadata.Id, new[] { sourceId });
        Assert.Contains(store.ShowRecord(newer.Metadata.Id).Metadata.RelatedRecords ?? Array.Empty<RelatedRecord>(), x => x.RecordId == earlier.Metadata.Id);
        var incoming = Assert.Single(store.ShowRecordRelations(earlier.Metadata.Id));
        Assert.Contains("incoming", JsonSerializer.Serialize(incoming));

        var currentPath = store.ShowRecord(newer.Metadata.Id).Path;
        var missingMarkdown = File.ReadAllText(currentPath).Replace(earlier.Metadata.Id.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal);
        store.EditRecord(newer.Metadata.Id, missingMarkdown);
        Assert.Throws<InvalidDataException>(() => store.AcceptRecord(newer.Metadata.Id, new[] { sourceId }));
    }

    [Fact]
    public void MigrationResumesPreparedJournalForward()
    {
        var input = Path.Combine(temp, "legacy.txt"); File.WriteAllText(input, "Migration recovery text.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value; var source = store.Show(sourceId);
        var settingsPath = Path.Combine(Db, "config", "settings.json"); var stagedPath = settingsPath + ".v2.new";
        var currentSettings = File.ReadAllText(settingsPath); var migratedSettings = "{\"schema_version\":\"2\",\"max_file_size_bytes\":52428800,\"max_extracted_text_bytes\":10485760}\n";
        File.WriteAllText(stagedPath, migratedSettings);
        static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var journal = new
        {
            format_version = 1,
            phase = "committing",
            completed = 0,
            changes = new[] { new { target = "config/settings.json", staged = "config/settings.json.v2.new", sha256 = Hash(migratedSettings), originalSha256 = Hash(currentSettings), kind = "settings" } }
        };
        File.WriteAllText(Path.Combine(Db, ".migration-v2.json"), JsonSerializer.Serialize(journal));
        store.Migrate(2);
        Assert.False(File.Exists(Path.Combine(Db, ".migration-v2.json")));
        Assert.Equal("2", JsonDocument.Parse(File.ReadAllText(Path.Combine(source.Directory, "source.json"))).RootElement.GetProperty("schema_version").GetString());
    }

    [Fact]
    public void MigrationStopsAndPreservesJournalWhenStagedFileHashIsWrong()
    {
        var store = new KnowledgeBaseStore(Db); store.Initialize();
        var settingsPath = Path.Combine(Db, "config", "settings.json"); var stagedPath = settingsPath + ".v2.new";
        var original = File.ReadAllText(settingsPath); var expected = "{\"schema_version\":\"2\",\"max_file_size_bytes\":52428800,\"max_extracted_text_bytes\":10485760}\n";
        File.WriteAllText(stagedPath, "tampered staged data");
        static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var journal = new { format_version = 1, phase = "committing", completed = 0, changes = new[] { new { target = "config/settings.json", staged = "config/settings.json.v2.new", sha256 = Hash(expected), originalSha256 = Hash(original), kind = "settings" } } };
        var journalPath = Path.Combine(Db, ".migration-v2.json"); File.WriteAllText(journalPath, JsonSerializer.Serialize(journal));
        Assert.Throws<InvalidDataException>(() => store.Migrate(2));
        Assert.True(File.Exists(journalPath)); Assert.True(File.Exists(stagedPath)); Assert.Equal(original, File.ReadAllText(settingsPath));
    }

    [Fact]
    public void Schema3MigrationDemotesEditedLegacyReviewedRecordAndOwnerCanReaccept()
    {
        var input = Path.Combine(temp, "legacy-reviewed.txt"); File.WriteAllText(input, "The original evidence sentence.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        store.Migrate(2);
        var draft = store.DraftCandidate(sourceId, "Legacy claim", "Original claim body.", 1, 1, "The original evidence sentence.");
        var reviewed = store.AcceptRecord(draft.Metadata.Id, new[] { sourceId });
        var downgraded = reviewed.Metadata with { SchemaVersion = "2" };
        var oldHash = KnowledgeRepository.ContentHash(downgraded, reviewed.Body);
        var markdown = File.ReadAllText(reviewed.Path).Replace(reviewed.Metadata.ReviewedContentSha256!, oldHash, StringComparison.Ordinal)
            .Replace("Original claim body.", "Edited claim body.", StringComparison.Ordinal);
        markdown = string.Join("\n", markdown.Split('\n').Select(line => line.StartsWith("schema_version:", StringComparison.Ordinal) ? "schema_version: \"2\"" : line));
        File.WriteAllText(reviewed.Path, markdown);
        store.Migrate(3);
        var needsReview = store.ShowRecord(reviewed.Metadata.Id);
        Assert.Equal("review_required", needsReview.Metadata.Status);
        Assert.Equal("record_content_changed", needsReview.Metadata.RejectionReason);
        Assert.Contains(needsReview.Metadata.History, x => x.Status == "review_required" && x.Reason == "record_content_changed");
        Assert.Throws<FileNotFoundException>(() => new AgentKnowledgeService(store, new byte[32]).GetRecord(reviewed.Metadata.Id, "external"));
        Assert.Equal("reviewed", store.AcceptRecord(reviewed.Metadata.Id, new[] { sourceId }).Metadata.Status);
    }

    [Fact]
    public void ExternalAgentAccessTracksCurrentSourceClassification()
    {
        var input = Path.Combine(temp, "policy.txt"); File.WriteAllText(input, "A cited policy line.\nMore context.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var text = File.ReadAllText(Path.Combine(store.Show(sourceId).Directory, "extracted.txt"));
        var draft = store.DraftCandidate(sourceId, "Policy claim", "Claim body", 1, 1, "A cited policy line.");
        store.AcceptRecord(draft.Metadata.Id, new[] { sourceId });
        var service = new AgentKnowledgeService(store, new byte[32]);
        Assert.Throws<FileNotFoundException>(() => service.GetRecord(draft.Metadata.Id, "external"));
        var revision = store.SetSourceAccess(sourceId, "external_allowed", "Reviewed for external use");
        var exposed = service.GetRecord(draft.Metadata.Id, "external");
        Assert.Equal("reviewed", exposed.Status); Assert.Equal(revision, exposed.PolicyRevision);
        var nextRevision = store.SetSourceAccess(sourceId, "local_only", "Owner restricted this source");
        Assert.True(nextRevision > revision);
        Assert.Throws<FileNotFoundException>(() => service.GetRecord(draft.Metadata.Id, "external"));
    }

    [Fact]
    public void ClientRegistryStoresOnlyTokenHashAndRevocationTakesEffect()
    {
        var path = Path.Combine(temp, "clients.json"); var registry = new ClientRegistry(path);
        var issued = registry.Create("external", new[] { "search", "read" });
        Assert.DoesNotContain(issued.Token, File.ReadAllText(path));
        Assert.Equal(issued.ClientId, registry.Authenticate(issued.Token)?.ClientId);
        Assert.True(registry.Revoke(issued.ClientId));
        Assert.Null(registry.Authenticate(issued.Token));
    }

    [Fact]
    public void ClientRegistrySerializesConcurrentWritersAcrossInstances()
    {
        var path = Path.Combine(temp, "concurrent-clients.json");
        var registries = Enumerable.Range(0, 8).Select(_ => new ClientRegistry(path)).ToArray();
        Parallel.For(0, registries.Length, i => registries[i].Create("local", new[] { "search" }));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(registries.Length, json.RootElement.GetArrayLength());
    }

    [Fact]
    public void IdempotencyStorePersistsStableReceiptAndRejectsDigestConflict()
    {
        var state = Path.Combine(temp, "gateway-state"); var first = new IdempotencyStore(state);
        var operation = first.Reserve("client-a", "draft-key", "digest-a", new[] { Guid.NewGuid() }, new[] { "sourcehash" }, out var replay);
        Assert.False(replay);
        var receipt = new DraftReceipt(operation.RecordId, "draft", operation.OperationId, 17);
        first.Update(operation, "record_saved", receipt); first.Update(operation with { Phase = "record_saved", Receipt = receipt }, "completed", receipt);
        var restarted = new IdempotencyStore(state);
        var repeated = restarted.Reserve("client-a", "draft-key", "digest-a", operation.SourceIds, operation.DependencyHashes!, out replay);
        Assert.True(replay);
        Assert.Equal(operation.RecordId, repeated.Receipt!.Id);
        Assert.Equal(receipt, repeated.Receipt);
        Assert.Throws<InvalidOperationException>(() => restarted.Reserve("client-a", "draft-key", "digest-b", operation.SourceIds, operation.DependencyHashes!, out _));
    }

    [Fact]
    public void AgentSearchCursorIsOpaqueBoundAndExpiresOnPolicyRevisionChange()
    {
        var input = Path.Combine(temp, "cursor-source.txt"); File.WriteAllText(input, "Cursor evidence line.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        foreach (var title in new[] { "Cursor claim one", "Cursor claim two" })
        {
            var draft = store.DraftCandidate(sourceId, title, "Cursorword searchable body.", 1, 1, "Cursor evidence line.");
            store.AcceptRecord(draft.Metadata.Id, new[] { sourceId });
        }
        store.SetSourceAccess(sourceId, "external_allowed", "Allowed for cursor test");
        var service = new AgentKnowledgeService(store, new byte[32]);
        var first = service.Search("cursorword", 1, null, "external", "client-one", new[] { "search" });
        Assert.NotNull(first.Cursor); Assert.DoesNotContain("offset", first.Cursor!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(first.Results);
        Assert.Throws<ArgumentException>(() => service.Search("cursorword", 1, first.Cursor, "external", "client-two", new[] { "search" }));
        store.SetSourceAccess(sourceId, "local_only", "Revoke cursor visibility");
        Assert.Throws<IOException>(() => service.Search("cursorword", 1, first.Cursor, "external", "client-one", new[] { "search" }));
    }

    [Fact]
    public void LocalExtractorKeepsValidPartialResultsAndRejectsMalformedOrOversizedProcessOutput()
    {
        var input = Path.Combine(temp, "extractor-source.txt"); File.WriteAllText(input, "Extractor evidence line.\nSecond line.");
        var store = new KnowledgeBaseStore(Db); var sourceId = store.Import(input).Items.Single().SourceId!.Value;
        var success = RunExtractor(sourceId, "valid_mixed");
        Assert.Equal(0, success.ExitCode);
        var result = JsonDocument.Parse(success.Stdout).RootElement;
        Assert.Single(result.GetProperty("created").EnumerateArray());
        Assert.Equal(2, result.GetProperty("errors").GetArrayLength());
        Assert.NotNull(result.GetProperty("created")[0].GetProperty("metadata").GetProperty("operation_id").GetString());
        Assert.Equal("local-process-v1", result.GetProperty("created")[0].GetProperty("metadata").GetProperty("adapter_version").GetString());
        Assert.Single(store.ListRecords());

        foreach (var mode in new[] { "no_read", "over_stdout", "over_stderr", "exit_error", "malformed_json" })
        {
            var failed = RunExtractor(sourceId, mode);
            Assert.NotEqual(0, failed.ExitCode);
            Assert.Single(store.ListRecords());
        }
    }

    [Fact]
    public async Task ExtractorRunnerKillsAProcessThatDoesNotReadInputWhenTimedOut()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory); var targetFramework = output.Name; var configuration = output.Parent!.Name;
        var child = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../ExtractorTestProcess/bin/{configuration}/{targetFramework}/ExtractorTestProcess.dll"));
        Assert.True(File.Exists(child), $"Expected test extractor at {child}");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet"); start.ArgumentList.Add(child); start.ArgumentList.Add("no_read_wait");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ExtractorProcessRunner.RunAsync(start, "input", TimeSpan.FromMilliseconds(300)));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
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
        => RunCliWithEnvironment(null, arguments);

    private (int ExitCode, string Stdout, string Stderr) RunExtractor(Guid sourceId, string mode)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var targetFramework = output.Name;
        var configuration = output.Parent?.Name ?? throw new InvalidOperationException("Could not determine test build configuration");
        var child = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../ExtractorTestProcess/bin/{configuration}/{targetFramework}/ExtractorTestProcess.dll"));
        if (!File.Exists(child)) throw new FileNotFoundException("Extractor test child was not built for the current configuration", child);
        var environment = new Dictionary<string, string>
        {
            ["KB_LOCAL_EXTRACTOR_EXECUTABLE"] = "dotnet",
            ["KB_LOCAL_EXTRACTOR_ARGUMENTS"] = JsonSerializer.Serialize(new[] { child, mode })
        };
        return RunCliWithEnvironment(environment, "--json", "--db", Db, "extract", "--source", sourceId.ToString());
    }

    private (int ExitCode, string Stdout, string Stderr) RunCliWithEnvironment(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "KnowledgeBase.Cli.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment.Remove("EDITOR"); start.Environment.Remove("VISUAL");
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
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
        public (int Indexed, int Skipped, int KnowledgeIndexed) Rebuild(IEnumerable<(SourceRecord Record, string Directory)> sources, int expected, IReadOnlyCollection<KnowledgeRecordView>? knowledge = null) => inner.Rebuild(sources, expected, knowledge);
        public void IndexKnowledge(KnowledgeRecordView record) => inner.IndexKnowledge(record);
        public IReadOnlyList<KnowledgeSearchResult> SearchKnowledge(string query, int limit, string? status) => inner.SearchKnowledge(query, limit, status);
        public IReadOnlyList<string> VerifyKnowledge(IReadOnlyCollection<KnowledgeRecordView> records) => inner.VerifyKnowledge(records);
        public (string Integrity, int Missing, int Extra, int Count) Verify(IReadOnlyCollection<SourceRecord> records) => inner.Verify(records);
        public IReadOnlyList<SearchResult> Search(string query, int limit, string? sourceType, DateTimeOffset? from, DateTimeOffset? to, string? status) => inner.Search(query, limit, sourceType, from, to, status);
    }

    private sealed class FailingKnowledgeIndex(ISearchIndex inner) : ISearchIndex
    {
        public string DatabasePath => inner.DatabasePath;
        public void Ensure() => inner.Ensure();
        public void Index(SourceRecord record, string body) => inner.Index(record, body);
        public (int Indexed, int Skipped, int KnowledgeIndexed) Rebuild(IEnumerable<(SourceRecord Record, string Directory)> sources, int expected, IReadOnlyCollection<KnowledgeRecordView>? knowledge = null) => inner.Rebuild(sources, expected, knowledge);
        public void IndexKnowledge(KnowledgeRecordView record) => throw new IOException("injected knowledge index failure");
        public IReadOnlyList<KnowledgeSearchResult> SearchKnowledge(string query, int limit, string? status) => inner.SearchKnowledge(query, limit, status);
        public IReadOnlyList<string> VerifyKnowledge(IReadOnlyCollection<KnowledgeRecordView> records) => inner.VerifyKnowledge(records);
        public (string Integrity, int Missing, int Extra, int Count) Verify(IReadOnlyCollection<SourceRecord> records) => inner.Verify(records);
        public IReadOnlyList<SearchResult> Search(string query, int limit, string? sourceType, DateTimeOffset? from, DateTimeOffset? to, string? status) => inner.Search(query, limit, sourceType, from, to, status);
    }
}
