using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using KnowledgeBase.Core;

var defaultDatabase = Environment.GetEnvironmentVariable("KB_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".knowledge-base");
var databaseOption = new Option<string>("--db") { Description = "Knowledge-base directory", DefaultValueFactory = _ => defaultDatabase };
var jsonOption = new Option<bool>("--json") { Description = "Write machine-readable JSON" };
var verboseOption = new Option<bool>("--verbose") { Description = "Include technical error details" };
var root = new RootCommand("Local personal knowledge base");
root.Options.Add(databaseOption); root.Options.Add(jsonOption); root.Options.Add(verboseOption);

var initPath = new Argument<string?>("path") { Description = "Knowledge-base directory" };
var init = new Command("init", "Create a knowledge base"); init.Arguments.Add(initPath);
init.SetAction(parse => Run(() =>
{
    var path = parse.GetValue(initPath) ?? parse.GetValue(databaseOption)!;
    new KnowledgeBaseStore(path).Initialize(); return new { path = Path.GetFullPath(path), schema_version = "2" };
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

var importPath = new Argument<string>("path") { Description = "File or directory to import" };
var import = new Command("import", "Import a file or directory"); import.Arguments.Add(importPath);
import.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Import(parse.GetValue(importPath)!), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

var query = new Argument<string>("query") { Description = "Full-text search query" };
var typeOption = new Option<string?>("--type") { Description = "Filter by source type" };
var statusOption = new Option<string?>("--status") { Description = "Filter by extraction status" };
var fromOption = new Option<string?>("--from") { Description = "Imported on or after this date/time" };
var toOption = new Option<string?>("--to") { Description = "Imported through this date/time (inclusive date)" };
var limitOption = new Option<int>("--limit") { Description = "Maximum number of results", DefaultValueFactory = _ => 10 };
var scopeOption = new Option<string>("--scope") { Description = "Search scope: sources, knowledge, or all", DefaultValueFactory = _ => "sources" };
var recordStatusOption = new Option<string?>("--record-status") { Description = "Knowledge status filter; defaults to reviewed" };
var search = new Command("search", "Search indexed sources");
search.Arguments.Add(query); search.Options.Add(typeOption); search.Options.Add(statusOption); search.Options.Add(fromOption); search.Options.Add(toOption); search.Options.Add(limitOption); search.Options.Add(scopeOption); search.Options.Add(recordStatusOption);
search.SetAction(parse => Run<object>(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var scope = parse.GetValue(scopeOption)!;
    var sourceResults = scope is "sources" or "all" ? store.Search(parse.GetValue(query)!, parse.GetValue(limitOption), parse.GetValue(typeOption), ParseDate(parse.GetValue(fromOption), "--from", false), ParseDate(parse.GetValue(toOption), "--to", true), parse.GetValue(statusOption)) : Array.Empty<SearchResult>();
    var recordResults = scope is "knowledge" or "all" ? store.SearchKnowledge(parse.GetValue(query)!, parse.GetValue(limitOption), parse.GetValue(recordStatusOption) ?? "reviewed") : Array.Empty<KnowledgeSearchResult>();
    if (scope is not ("sources" or "knowledge" or "all")) throw new ArgumentException("--scope must be sources, knowledge, or all");
    return scope == "sources" ? sourceResults : scope == "knowledge" ? recordResults : new { sources = sourceResults, knowledge = recordResults };
},
    parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

var sourceId = new Argument<Guid>("source-id") { Description = "Source UUID" };
var textOption = new Option<bool>("--text") { Description = "Include extracted text" };
var show = new Command("show", "Show a source record"); show.Arguments.Add(sourceId); show.Options.Add(textOption);
show.SetAction(parse => Run(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var (record, dir) = store.Show(parse.GetValue(sourceId));
    var textPath = Path.Combine(dir, "extracted.txt");
    return new
    {
        metadata = record,
        original_path = Path.Combine(dir, record.OriginalRelativePath ?? "original.bin"),
        extracted_text_path = textPath,
        text = parse.GetValue(textOption) && File.Exists(textPath) ? File.ReadAllText(textPath) : null
    };
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

var verifyOption = new Option<bool>("--verify") { Description = "Check SQLite integrity and source consistency" };
var status = new Command("status", "Show database status"); status.Options.Add(verifyOption);
status.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Status(parse.GetValue(verifyOption)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var rebuild = new Command("rebuild-index", "Rebuild the full-text index");
rebuild.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Rebuild(), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

var migrateVersion = new Option<int>("--to") { Required = true, Description = "Target database schema version" };
var migrate = new Command("migrate", "Migrate the knowledge base"); migrate.Options.Add(migrateVersion);
migrate.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Migrate(parse.GetValue(migrateVersion)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

var recordsCommand = new Command("record", "Manage portable knowledge records");
var draft = new Command("draft", "Create a knowledge draft from a source");
var draftSource = new Option<Guid>("--source") { Required = true }; var draftTitle = new Option<string>("--title") { Required = true };
var draftBody = new Option<string>("--body") { DefaultValueFactory = _ => "<!-- Replace with a concise, evidence-based statement. -->\n" };
var draftType = new Option<string>("--type") { DefaultValueFactory = _ => "fact" }; var draftKind = new Option<string>("--kind") { DefaultValueFactory = _ => "source_claim" };
draft.Options.Add(draftSource); draft.Options.Add(draftTitle); draft.Options.Add(draftBody); draft.Options.Add(draftType); draft.Options.Add(draftKind);
draft.SetAction(parse => Run(() => CreateManualDraft(new KnowledgeBaseStore(parse.GetValue(databaseOption)!), parse.GetValue(draftSource), parse.GetValue(draftTitle)!, parse.GetValue(draftBody)!, parse.GetValue(draftType)!, parse.GetValue(draftKind)!), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var recordStatus = new Option<string?>("--status"); var listRecords = new Command("list", "List knowledge records"); listRecords.Options.Add(recordStatus);
listRecords.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).ListRecords(parse.GetValue(recordStatus)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var recordId = new Argument<Guid>("record-id"); var sourcesOption = new Option<bool>("--sources"); var showRecord = new Command("show", "Show a knowledge record"); showRecord.Arguments.Add(recordId); showRecord.Options.Add(sourcesOption);
showRecord.SetAction(parse => Run<object>(() => { var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var value = store.ShowRecord(parse.GetValue(recordId)); return parse.GetValue(sourcesOption) ? new { record = value, sources = store.ShowRecordSources(parse.GetValue(recordId)) } : value; }, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var editRecord = new Command("edit", "Edit a knowledge record in the configured editor"); editRecord.Arguments.Add(recordId);
editRecord.SetAction(parse => Run<object>(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var current = store.ShowRecord(parse.GetValue(recordId));
    var editor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR");
    if (string.IsNullOrWhiteSpace(editor)) return new { path = current.Path, message = "Set VISUAL or EDITOR to edit this draft" };
    using var temp = new TemporaryEditFile(current.Path); var commandParts = editor.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var start = new System.Diagnostics.ProcessStartInfo(commandParts[0]) { UseShellExecute = false };
    if (commandParts.Length > 1) foreach (var argument in commandParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(argument);
    start.ArgumentList.Add(temp.Path);
    using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException($"Editor exited with code {process.ExitCode}");
    return store.EditRecord(parse.GetValue(recordId), File.ReadAllText(temp.Path));
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var confirmSource = new Option<Guid[]>("--confirm-source") { AllowMultipleArgumentsPerToken = true }; var accept = new Command("accept", "Accept a draft after confirming every cited source"); accept.Arguments.Add(recordId); accept.Options.Add(confirmSource);
accept.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).AcceptRecord(parse.GetValue(recordId), parse.GetValue(confirmSource) ?? Array.Empty<Guid>()), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var reason = new Option<string>("--reason") { Required = true }; var reject = new Command("reject", "Archive a draft with a reason"); reject.Arguments.Add(recordId); reject.Options.Add(reason);
reject.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).RejectRecord(parse.GetValue(recordId), parse.GetValue(reason)!), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
recordsCommand.Subcommands.Add(draft); recordsCommand.Subcommands.Add(listRecords); recordsCommand.Subcommands.Add(showRecord); recordsCommand.Subcommands.Add(editRecord); recordsCommand.Subcommands.Add(accept); recordsCommand.Subcommands.Add(reject);

var extract = new Command("extract", "Create draft suggestions with an explicitly configured local process"); var extractSource = new Option<Guid>("--source") { Required = true }; extract.Options.Add(extractSource);
extract.SetAction(parse => Run(() => InvokeLocalExtractor(new KnowledgeBaseStore(parse.GetValue(databaseOption)!), parse.GetValue(extractSource)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

root.Subcommands.Add(init); root.Subcommands.Add(import); root.Subcommands.Add(search); root.Subcommands.Add(show); root.Subcommands.Add(status); root.Subcommands.Add(rebuild); root.Subcommands.Add(migrate); root.Subcommands.Add(recordsCommand); root.Subcommands.Add(extract);
var parseResult = root.Parse(args);
var jsonRequested = args.Contains("--json", StringComparer.Ordinal);
if (parseResult.Errors.Count > 0)
{
    var errors = parseResult.Errors.Select(e => e.Message).ToArray();
    if (jsonRequested) Console.WriteLine(JsonSerializer.Serialize(new { error = "argument_error", messages = errors }));
    else { foreach (var error in errors) Console.Error.WriteLine($"Error: {error}"); Console.Error.WriteLine("Use 'kb --help' for command usage."); }
    return 2;
}
return parseResult.Invoke();

static int Run<T>(Func<T> action, bool json, bool verbose)
{
    try
    {
        var result = action();
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        if (json)
        {
            object? output = result;
            if (result is ImportSummary importSummary)
                output = new
                {
                    summary = new { importSummary.Imported, importSummary.Duplicates, importSummary.Indexed, importSummary.Unsupported, importSummary.Errors, importSummary.Oversized, importSummary.SkippedSymlinks },
                    items = importSummary.Items.Select(i => new { i.Path, i.Status, i.SourceId, i.Message, Details = verbose ? i.Details : null })
                };
            Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
        }
        else PrintHuman(result);
        if (result is ImportSummary summary && (summary.Errors > 0 || summary.Oversized > 0)) return 1;
        if (result?.GetType().GetProperty("verify_ok")?.GetValue(result) is false) return 1;
        return 0;
    }
    catch (Exception ex)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, details = verbose ? ex.ToString() : null }));
        else { Console.Error.WriteLine($"Error: {ex.Message}"); if (verbose) Console.Error.WriteLine(ex); }
        return 1;
    }
}

static void PrintHuman<T>(T result)
{
    switch (result)
    {
        case ImportSummary s:
            Console.WriteLine($"Import: {s.Imported} imported, {s.Duplicates} duplicates, {s.Indexed} indexed, {s.Unsupported} unsupported, {s.Errors} errors, {s.Oversized} oversized, {s.SkippedSymlinks} symlinks skipped.");
            foreach (var item in s.Items.Where(x => x.Message is not null || x.Status is "unsupported" or "empty_text"))
                Console.Error.WriteLine($"{item.Status}: {item.Path}{(item.Message is null ? "" : $" — {item.Message}")}{(item.SourceId is null ? "" : $" [{item.SourceId}]")}");
            break;
        case IReadOnlyList<SearchResult> results:
            if (results.Count == 0) Console.WriteLine("No results.");
            foreach (var x in results) Console.WriteLine($"{x.SourceId}  {x.OriginalName}  {x.ImportedAt:u}\n  {x.Snippet}\n  {x.SourcePath}");
            break;
        case IReadOnlyList<KnowledgeSearchResult> knowledge:
            if (knowledge.Count == 0) Console.WriteLine("No results.");
            foreach (var x in knowledge) Console.WriteLine($"{x.Id}  {x.Title}  {x.Status}\n  {x.Snippet}\n  sources: {string.Join(", ", x.SourceIds)}");
            break;
        case RebuildResult rebuilt: Console.WriteLine($"Index rebuilt: {rebuilt.Indexed} indexed, {rebuilt.Skipped} skipped."); break;
        default:
            using (var document = JsonDocument.Parse(JsonSerializer.Serialize(result))) PrintProperties(document.RootElement, 0);
            break;
    }
}

static void PrintProperties(JsonElement element, int depth)
{
    var indent = new string(' ', depth * 2);
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                Console.WriteLine($"{indent}{property.Name}:"); PrintProperties(property.Value, depth + 1);
            }
            else Console.WriteLine($"{indent}{property.Name}: {property.Value.ToString()}");
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray()) { Console.WriteLine($"{indent}-"); PrintProperties(item, depth + 1); }
    }
    else Console.WriteLine($"{indent}{element}");
}

static DateTimeOffset? ParseDate(string? value, string option, bool inclusiveDate)
{
    if (value is null) return null;
    if (DateOnly.TryParseExact(value, "yyyy-MM-dd", out var day))
        return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero).AddDays(inclusiveDate ? 1 : 0);
    if (DateTimeOffset.TryParse(value, out var date)) return date.ToUniversalTime().AddTicks(inclusiveDate ? 1 : 0);
    throw new ArgumentException($"Invalid date for {option}: expected YYYY-MM-DD or ISO 8601 timestamp");
}

static object InvokeLocalExtractor(KnowledgeBaseStore store, Guid sourceId)
{
    var command = Environment.GetEnvironmentVariable("KB_LOCAL_EXTRACTOR");
    if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Set KB_LOCAL_EXTRACTOR to an explicitly configured local extractor command");
    var (record, directory) = store.Show(sourceId); var textPath = Path.Combine(directory, "extracted.txt");
    if (!File.Exists(textPath)) throw new InvalidDataException($"Source {sourceId}: extracted text is unavailable");
    var text = File.ReadAllText(textPath).Replace("\r\n", "\n", StringComparison.Ordinal);
    var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries); var start = new System.Diagnostics.ProcessStartInfo(args[0]) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args.Skip(1)) start.ArgumentList.Add(arg);
    using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start local extractor");
    var outputTask = Task.Run(() => ReadBounded(process.StandardOutput.BaseStream, 4 * 1024 * 1024));
    var errorTask = Task.Run(() => ReadBounded(process.StandardError.BaseStream, 64 * 1024));
    var request = new { schema_version = "1", operation_id = Guid.NewGuid(), source_id = sourceId, text_sha256 = record.ExtractedTextSha256, text };
    process.StandardInput.Write(JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })); process.StandardInput.Close();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
    catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException("Local extractor exceeded the 120 second limit"); }
    var output = outputTask.GetAwaiter().GetResult(); var stderr = errorTask.GetAwaiter().GetResult();
    if (output.Length > 4 * 1024 * 1024) throw new InvalidDataException("Local extractor response exceeds 4 MiB");
    if (process.ExitCode != 0) throw new InvalidOperationException($"Local extractor exited with code {process.ExitCode}: {Encoding.UTF8.GetString(stderr)}");
    using var response = JsonDocument.Parse(output); var root = response.RootElement;
    if (!root.TryGetProperty("schema_version", out var version) || version.GetString() != "1" || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 50)
        throw new InvalidDataException("Local extractor returned an invalid protocol response (expected schema_version 1 and up to 50 candidates)");
    var created = new List<KnowledgeRecordView>(); var duplicates = new List<Guid>(); var existing = store.ListRecords();
    foreach (var item in candidates.EnumerateArray())
    {
        var title = item.GetProperty("title").GetString() ?? throw new InvalidDataException("Candidate title is required");
        var body = item.GetProperty("body").GetString() ?? throw new InvalidDataException("Candidate body is required");
        var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "fact" : "fact"; var kind = item.TryGetProperty("kind", out var k) ? k.GetString() ?? "source_claim" : "source_claim";
        var startLine = item.GetProperty("start_line").GetInt32(); var endLine = item.GetProperty("end_line").GetInt32(); var quote = item.GetProperty("quote").GetString() ?? "";
        var duplicate = existing.FirstOrDefault(x => x.Metadata.Title.Equals(title, StringComparison.OrdinalIgnoreCase) && x.Body.Equals(body, StringComparison.Ordinal));
        if (duplicate is not null) { duplicates.Add(duplicate.Metadata.Id); continue; }
        created.Add(store.DraftCandidate(sourceId, title, body, startLine, endLine, quote, type, kind));
    }
    return new { operation_id = request.operation_id, created, possible_duplicates = duplicates, adapter_stderr = Encoding.UTF8.GetString(stderr) };
}

static object CreateManualDraft(KnowledgeBaseStore store, Guid sourceId, string title, string body, string type, string kind)
{
    var record = store.DraftRecord(sourceId, title, body, type, kind);
    var editor = Environment.GetEnvironmentVariable("VISUAL") ?? Environment.GetEnvironmentVariable("EDITOR");
    if (string.IsNullOrWhiteSpace(editor)) return new { record, path = record.Path, message = "Set VISUAL or EDITOR to edit this draft" };
    using var temp = new TemporaryEditFile(record.Path); var commandParts = editor.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var start = new System.Diagnostics.ProcessStartInfo(commandParts[0]) { UseShellExecute = false };
    if (commandParts.Length > 1) foreach (var argument in commandParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(argument);
    start.ArgumentList.Add(temp.Path); using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"Editor exited with code {process.ExitCode}");
    return store.EditRecord(record.Metadata.Id, File.ReadAllText(temp.Path));
}

static byte[] ReadBounded(Stream stream, int maxBytes)
{
    using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) { var remaining = maxBytes + 1 - (int)output.Length; if (remaining > 0) output.Write(buffer, 0, Math.Min(remaining, read)); }
    return output.ToArray();
}

sealed class TemporaryEditFile : IDisposable
{
    public string Path { get; }
    public TemporaryEditFile(string source)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kb-edit-" + Guid.NewGuid().ToString("N") + ".md");
        File.Copy(source, Path);
    }
    public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
}
