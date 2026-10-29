using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Security.Cryptography;
using KnowledgeBase.Core;
using KnowledgeBase.Configuration;

var applicationConfiguration = ApplicationConfiguration.Load();
var cliSettings = CliSettings.Load(applicationConfiguration);
var defaultDatabase = cliSettings.DefaultDatabasePath;
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

var access = new Command("access", "Manage source and record access classifications");
var accessSource = new Option<Guid?>("--source") { Description = "Source identifier (GUID)" }; var accessRecord = new Option<Guid?>("--record") { Description = "Record identifier (GUID)" };
var accessShow = new Command("show", "Show current and effective access"); accessShow.Options.Add(accessSource); accessShow.Options.Add(accessRecord);
accessShow.SetAction(parse => Run<object>(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var source = parse.GetValue(accessSource); var record = parse.GetValue(accessRecord);
    if ((source is null) == (record is null)) throw new ArgumentException("Specify exactly one of --source or --record");
    if (source is Guid sourceId) { var value = store.Show(sourceId).Record; return new { source_id = sourceId, access = value.Access, policy_revision = store.PolicyRevision }; }
    var view = store.ShowRecord(record!.Value); var effective = store.EffectiveAccess(view.Metadata);
    return new { record_id = view.Metadata.Id, access_override = view.Metadata.AccessOverride, effective_access = effective, policy_revision = store.PolicyRevision };
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var accessSet = new Command("set", "Set an owner classification override"); var accessLevel = new Option<string>("--level") { Required = true, Description = "Access level: local_only, unclassified, or external_allowed" }; var accessReason = new Option<string>("--reason") { Required = true, Description = "Reason for the access change; recorded in policy history" };
accessSet.Options.Add(accessSource); accessSet.Options.Add(accessRecord); accessSet.Options.Add(accessLevel); accessSet.Options.Add(accessReason);
accessSet.SetAction(parse => Run<object>(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var source = parse.GetValue(accessSource); var record = parse.GetValue(accessRecord);
    if ((source is null) == (record is null)) throw new ArgumentException("Specify exactly one of --source or --record");
    var level = parse.GetValue(accessLevel)!; if (level is not ("local_only" or "unclassified" or "external_allowed")) throw new ArgumentException("--level must be local_only, unclassified, or external_allowed");
    var revision = source is Guid sourceId ? store.SetSourceAccess(sourceId, level, parse.GetValue(accessReason)!) : store.SetRecordAccessOverride(record!.Value, level, parse.GetValue(accessReason)!);
    return new { policy_revision = revision, access = level };
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var accessClear = new Command("clear", "Remove a record access override"); accessClear.Options.Add(accessRecord); accessClear.Options.Add(accessReason);
accessClear.SetAction(parse => Run<object>(() => { var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var id = parse.GetValue(accessRecord) ?? throw new ArgumentException("--record is required"); return new { policy_revision = store.SetRecordAccessOverride(id, null, parse.GetValue(accessReason)!), access_override = (string?)null }; }, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
access.Subcommands.Add(accessShow); access.Subcommands.Add(accessSet); access.Subcommands.Add(accessClear);

var clients = new Command("client", "Manage agent API clients"); var profileOption = new Option<string>("--profile") { Required = true, Description = "Client profile: local or external" }; var scopesOption = new Option<string>("--scopes") { Required = true, Description = "Comma-separated scopes: search, read, draft" };
var clientCreate = new Command("create", "Create a bearer credential (shown once)"); clientCreate.Options.Add(profileOption); clientCreate.Options.Add(scopesOption);
clientCreate.SetAction(parse => Run<object>(() =>
{
    var issued = new ClientRegistry(cliSettings.ResolveClientRegistryPath()).Create(parse.GetValue(profileOption)!, parse.GetValue(scopesOption)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    return issued;
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var clientRevoke = new Command("revoke", "Revoke an agent API client"); var clientId = new Argument<string>("client-id"); clientRevoke.Arguments.Add(clientId);
clientRevoke.SetAction(parse => Run<object>(() =>
{
    if (!new ClientRegistry(cliSettings.ResolveClientRegistryPath()).Revoke(parse.GetValue(clientId)!)) throw new FileNotFoundException("Client not found"); return new { revoked = true };
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
clients.Subcommands.Add(clientCreate); clients.Subcommands.Add(clientRevoke);

var recordsCommand = new Command("record", "Manage portable knowledge records");
var draft = new Command("draft", "Create a knowledge draft from a source");
var draftSource = new Option<Guid>("--source") { Required = true }; var draftTitle = new Option<string>("--title") { Required = true };
var draftBody = new Option<string>("--body") { DefaultValueFactory = _ => "<!-- Replace with a concise, evidence-based statement. -->\n" };
var draftType = new Option<string>("--type") { DefaultValueFactory = _ => "fact" }; var draftKind = new Option<string>("--kind") { DefaultValueFactory = _ => "source_claim" };
var draftStartLine = new Option<int>("--start-line") { Required = true }; var draftEndLine = new Option<int>("--end-line") { Required = true }; var draftQuote = new Option<string>("--quote") { Required = true };
draft.Options.Add(draftSource); draft.Options.Add(draftTitle); draft.Options.Add(draftBody); draft.Options.Add(draftType); draft.Options.Add(draftKind); draft.Options.Add(draftStartLine); draft.Options.Add(draftEndLine); draft.Options.Add(draftQuote);
draft.SetAction(parse => Run(() => CreateManualDraft(new KnowledgeBaseStore(parse.GetValue(databaseOption)!), parse.GetValue(draftSource), parse.GetValue(draftTitle)!, parse.GetValue(draftBody)!, parse.GetValue(draftType)!, parse.GetValue(draftKind)!, parse.GetValue(draftStartLine), parse.GetValue(draftEndLine), parse.GetValue(draftQuote)!, cliSettings), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var recordStatus = new Option<string?>("--status"); var listRecords = new Command("list", "List knowledge records"); listRecords.Options.Add(recordStatus);
listRecords.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).ListRecords(parse.GetValue(recordStatus)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var recordId = new Argument<Guid>("record-id"); var sourcesOption = new Option<bool>("--sources"); var showRecord = new Command("show", "Show a knowledge record"); showRecord.Arguments.Add(recordId); showRecord.Options.Add(sourcesOption);
showRecord.SetAction(parse => Run<object>(() => { var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var value = store.ShowRecord(parse.GetValue(recordId)); return new { record = value, related_records = store.ShowRecordRelations(parse.GetValue(recordId)), sources = parse.GetValue(sourcesOption) ? store.ShowRecordSources(parse.GetValue(recordId)) : null }; }, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var editRecord = new Command("edit", "Edit a knowledge record in the configured editor"); editRecord.Arguments.Add(recordId);
editRecord.SetAction(parse => Run<object>(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!); var current = store.ShowRecord(parse.GetValue(recordId));
    var editor = cliSettings.PreferredEditor;
    if (string.IsNullOrWhiteSpace(editor)) return new { path = current.Path, message = "Set VISUAL or EDITOR to edit this draft" };
    using var temp = new TemporaryEditFile(current.Path); var expectedHash = KnowledgeRepository.FileHash(File.ReadAllText(current.Path)); var commandParts = editor.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var start = new System.Diagnostics.ProcessStartInfo(commandParts[0]) { UseShellExecute = false };
    if (commandParts.Length > 1) foreach (var argument in commandParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(argument);
    start.ArgumentList.Add(temp.Path);
    using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException($"Editor exited with code {process.ExitCode}");
    try { return store.EditRecord(parse.GetValue(recordId), File.ReadAllText(temp.Path), expectedHash); }
    catch (IOException) { temp.Preserve(); throw; }
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var confirmSource = new Option<Guid[]>("--confirm-source") { AllowMultipleArgumentsPerToken = true }; var accept = new Command("accept", "Accept a draft after confirming every cited source"); accept.Arguments.Add(recordId); accept.Options.Add(confirmSource);
accept.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).AcceptRecord(parse.GetValue(recordId), parse.GetValue(confirmSource) ?? Array.Empty<Guid>()), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var reason = new Option<string>("--reason") { Required = true }; var reject = new Command("reject", "Archive a draft with a reason"); reject.Arguments.Add(recordId); reject.Options.Add(reason);
reject.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).RejectRecord(parse.GetValue(recordId), parse.GetValue(reason)!), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
recordsCommand.Subcommands.Add(draft); recordsCommand.Subcommands.Add(listRecords); recordsCommand.Subcommands.Add(showRecord); recordsCommand.Subcommands.Add(editRecord); recordsCommand.Subcommands.Add(accept); recordsCommand.Subcommands.Add(reject);

var feedbackCommand = new Command("feedback", "Manage the local feedback journal");
var feedbackAdd = new Command("add", "Save feedback from a local file");
var feedbackWorkflow = new Option<string>("--workflow") { Required = true };
var feedbackRating = new Option<string>("--rating") { Required = true };
var feedbackCategory = new Option<string>("--category") { Required = true };
var feedbackNoteFile = new Option<string>("--note-file") { Required = true };
var feedbackExpectedFile = new Option<string?>("--expected-file");
var feedbackRecord = new Option<Guid?>("--record"); var feedbackSource = new Option<Guid?>("--source");
var feedbackRequestId = new Option<string?>("--request-id"); var feedbackClient = new Option<string?>("--client"); var feedbackModel = new Option<string?>("--model");
feedbackAdd.Options.Add(feedbackWorkflow); feedbackAdd.Options.Add(feedbackRating); feedbackAdd.Options.Add(feedbackCategory); feedbackAdd.Options.Add(feedbackNoteFile);
feedbackAdd.Options.Add(feedbackExpectedFile); feedbackAdd.Options.Add(feedbackRecord); feedbackAdd.Options.Add(feedbackSource); feedbackAdd.Options.Add(feedbackRequestId); feedbackAdd.Options.Add(feedbackClient); feedbackAdd.Options.Add(feedbackModel);
feedbackAdd.SetAction(parse => Run(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!);
    var notePath = parse.GetValue(feedbackNoteFile)!;
    if (!File.Exists(notePath)) throw new FileNotFoundException("Feedback note file does not exist", notePath);
    var expectedPath = parse.GetValue(feedbackExpectedFile);
    if (expectedPath is not null && !File.Exists(expectedPath)) throw new FileNotFoundException("Expected-result file does not exist", expectedPath);
    var result = store.Feedback.Add(parse.GetValue(feedbackWorkflow)!, parse.GetValue(feedbackRating)!, parse.GetValue(feedbackCategory)!, File.ReadAllText(notePath, Encoding.UTF8),
        expectedPath is null ? null : File.ReadAllText(expectedPath, Encoding.UTF8), parse.GetValue(feedbackRecord), parse.GetValue(feedbackSource), parse.GetValue(feedbackRequestId), parse.GetValue(feedbackClient), parse.GetValue(feedbackModel));
    foreach (var id in result.MissingRecords) Console.Error.WriteLine($"Warning: record {id} is not available in this knowledge base; feedback reference was kept.");
    foreach (var id in result.MissingSources) Console.Error.WriteLine($"Warning: source {id} is not available in this knowledge base; feedback reference was kept.");
    return result;
}, parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

var feedbackStatusFilter = new Option<string?>("--status"); var feedbackList = new Command("list", "List local feedback"); feedbackList.Options.Add(feedbackStatusFilter);
feedbackList.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Feedback.List(parse.GetValue(feedbackStatusFilter)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var feedbackId = new Argument<Guid>("id"); var feedbackShow = new Command("show", "Show local feedback"); feedbackShow.Arguments.Add(feedbackId);
feedbackShow.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Feedback.Show(parse.GetValue(feedbackId)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
var feedbackSetStatus = new Command("set-status", "Change local feedback status"); var feedbackNewStatus = new Option<string>("--status") { Required = true }; var feedbackReason = new Option<string>("--reason") { Required = true };
feedbackSetStatus.Arguments.Add(feedbackId); feedbackSetStatus.Options.Add(feedbackNewStatus); feedbackSetStatus.Options.Add(feedbackReason);
feedbackSetStatus.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Feedback.SetStatus(parse.GetValue(feedbackId), parse.GetValue(feedbackNewStatus)!, parse.GetValue(feedbackReason)!), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));
feedbackCommand.Subcommands.Add(feedbackAdd); feedbackCommand.Subcommands.Add(feedbackList); feedbackCommand.Subcommands.Add(feedbackShow); feedbackCommand.Subcommands.Add(feedbackSetStatus);

var extract = new Command("extract", "Create draft suggestions with an explicitly configured local process"); var extractSource = new Option<Guid>("--source") { Required = true }; extract.Options.Add(extractSource);
extract.SetAction(parse => Run(() => InvokeLocalExtractor(new KnowledgeBaseStore(parse.GetValue(databaseOption)!), parse.GetValue(extractSource), LocalExtractorSettings.Load(applicationConfiguration)), parse.GetValue(jsonOption), parse.GetValue(verboseOption)));

root.Subcommands.Add(init); root.Subcommands.Add(import); root.Subcommands.Add(search); root.Subcommands.Add(show); root.Subcommands.Add(status); root.Subcommands.Add(rebuild); root.Subcommands.Add(migrate); root.Subcommands.Add(access); root.Subcommands.Add(clients); root.Subcommands.Add(recordsCommand); root.Subcommands.Add(feedbackCommand); root.Subcommands.Add(extract);
var parseResult = root.Parse(args);
var jsonRequested = args.Contains("--json", StringComparer.Ordinal);
if (parseResult.Errors.Count > 0)
{
    var errors = parseResult.Errors.Select(e => e.Message).ToArray();
    if (jsonRequested) Console.WriteLine(JsonSerializer.Serialize(new { error = "argument_error", messages = errors }, CreateJsonOutputOptions()));
    else { foreach (var error in errors) Console.Error.WriteLine($"Error: {error}"); Console.Error.WriteLine("Use 'kb --help' for command usage."); }
    return 2;
}
return parseResult.Invoke();

static int Run<T>(Func<T> action, bool json, bool verbose)
{
    try
    {
        var result = action();
        if (json)
        {
            object? output = result;
            if (result is ImportSummary importSummary)
                output = new
                {
                    summary = new { importSummary.Imported, importSummary.Duplicates, importSummary.Indexed, importSummary.Unsupported, importSummary.Errors, importSummary.Oversized, importSummary.SkippedSymlinks },
                    items = importSummary.Items.Select(i => new { i.Path, i.Status, i.SourceId, i.Message, Details = verbose ? i.Details : null })
                };
            Console.WriteLine(JsonSerializer.Serialize(output, CreateJsonOutputOptions()));
        }
        else PrintHuman(result);
        if (result is ImportSummary summary && (summary.Errors > 0 || summary.Oversized > 0)) return 1;
        if (result?.GetType().GetProperty("verify_ok")?.GetValue(result) is false) return 1;
        return 0;
    }
    catch (Exception ex)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, details = verbose ? ex.ToString() : null }, CreateJsonOutputOptions()));
        else { Console.Error.WriteLine($"Error: {ex.Message}"); if (verbose) Console.Error.WriteLine(ex); }
        return 1;
    }
}

static JsonSerializerOptions CreateJsonOutputOptions() => new()
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

static void PrintHuman<T>(T result)
{
    switch (result)
    {
        case FeedbackAddResult added:
            Console.WriteLine($"Feedback saved: {added.Feedback.Id} ({added.Feedback.Status})");
            break;
        case FeedbackRecord feedback: Console.WriteLine($"{feedback.Id}  {feedback.Status}  {feedback.Workflow}  {feedback.Rating}/{feedback.Category}\n  {feedback.Note}"); break;
        case IReadOnlyList<FeedbackRecord> feedbackItems:
            if (feedbackItems.Count == 0) Console.WriteLine("No feedback.");
            foreach (var item in feedbackItems) Console.WriteLine($"{item.Id}  {item.CreatedAt:u}  {item.Status}  {item.Workflow}  {item.Rating}/{item.Category}\n  {item.Note}");
            break;
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

static object InvokeLocalExtractor(KnowledgeBaseStore store, Guid sourceId, LocalExtractorSettings extractorSettings)
{
    var executable = extractorSettings.Executable;
    if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Set KB_LOCAL_EXTRACTOR_EXECUTABLE and optionally KB_LOCAL_EXTRACTOR_ARGUMENTS (a JSON string array) to explicitly configure a local extractor");
    var (record, directory) = store.Show(sourceId); var textPath = Path.Combine(directory, "extracted.txt");
    if (!File.Exists(textPath)) throw new InvalidDataException($"Source {sourceId}: extracted text is unavailable");
    var text = File.ReadAllText(textPath).Replace("\r\n", "\n", StringComparison.Ordinal);
    var actualTextHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    if (record.ExtractedTextSha256 is null || !string.Equals(actualTextHash, record.ExtractedTextSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Source {sourceId}: extracted text changed; reimport or repair the source before extraction");
    if (Encoding.UTF8.GetByteCount(text) > 10 * 1024 * 1024) throw new InvalidDataException("Local extractor input exceeds 10 MiB");
    var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
    var configuredArgs = extractorSettings.ArgumentsJson;
    if (!string.IsNullOrWhiteSpace(configuredArgs))
    {
        var args = JsonSerializer.Deserialize<string[]>(configuredArgs) ?? throw new InvalidDataException("KB_LOCAL_EXTRACTOR_ARGUMENTS must be a JSON array of strings");
        foreach (var arg in args) start.ArgumentList.Add(arg);
    }
    var request = new { schema_version = "1", operation_id = Guid.NewGuid(), source_id = sourceId, text_sha256 = record.ExtractedTextSha256, text };
    var modelVersion = extractorSettings.ModelVersion;
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancelHandler;
    ExtractorProcessResult processResult;
    try { processResult = ExtractorProcessRunner.RunAsync(start, JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }), TimeSpan.FromSeconds(120), cancellation.Token).GetAwaiter().GetResult(); }
    finally { Console.CancelKeyPress -= cancelHandler; }
    var output = processResult.StandardOutput; var stderr = processResult.StandardError;
    if (processResult.ExitCode != 0) throw new InvalidOperationException($"Local extractor exited with code {processResult.ExitCode}: {Encoding.UTF8.GetString(stderr)}");
    using var response = JsonDocument.Parse(output); var root = response.RootElement;
    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(x => x.Name is not ("schema_version" or "candidates")) || !root.TryGetProperty("schema_version", out var version) || version.GetString() != "1" || !root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 50)
        throw new InvalidDataException("Local extractor returned an invalid protocol response (expected schema_version 1 and up to 50 candidates)");
    var created = new List<KnowledgeRecordView>(); var duplicates = new List<Guid>(); var errors = new List<object>(); var existing = store.ListRecords();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var validated = new List<(int Index, string Title, string Body, string Type, string Kind, int Start, int End, string Quote)>();
    var candidateIndex = 0;
    foreach (var item in candidates.EnumerateArray())
    {
        var position = candidateIndex++;
        try
        {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Candidate must be an object");
        if (item.EnumerateObject().Any(x => x.Name is not ("title" or "body" or "type" or "kind" or "start_line" or "end_line" or "quote"))) throw new InvalidDataException("Candidate contains an unknown field");
        var title = RequiredString(item, "title"); var body = RequiredString(item, "body");
        var type = OptionalString(item, "type", "fact"); var kind = OptionalString(item, "kind", "source_claim");
        var startLine = item.GetProperty("start_line").GetInt32(); var endLine = item.GetProperty("end_line").GetInt32(); var quote = RequiredString(item, "quote");
        if (type is not ("fact" or "decision" or "meeting" or "task" or "risk" or "question")) throw new InvalidDataException($"Unsupported candidate type '{type}'");
        if (kind is not ("source_claim" or "inference")) throw new InvalidDataException($"Unsupported candidate kind '{kind}'");
        var textLines = text.Split('\n'); if (startLine < 1 || endLine < startLine || endLine > textLines.Length || string.Join("\n", textLines[(startLine - 1)..endLine]) != quote) throw new InvalidDataException("Citation range and quote do not match source text");
        var identity = title.Trim().ToUpperInvariant() + "\0" + body;
        if (!seen.Add(identity)) { errors.Add(new { candidate = position, error = "duplicate_candidate_in_response" }); continue; }
        var duplicate = existing.FirstOrDefault(x => x.Metadata.Title.Equals(title, StringComparison.OrdinalIgnoreCase) && x.Body.Equals(body, StringComparison.Ordinal));
        if (duplicate is not null) { duplicates.Add(duplicate.Metadata.Id); continue; }
        validated.Add((position, title, body, type, kind, startLine, endLine, quote));
        }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        { errors.Add(new { candidate = position, error = ex.Message }); }
    }
    foreach (var candidate in validated)
    {
        try { created.Add(store.DraftCandidate(sourceId, candidate.Title, candidate.Body, candidate.Start, candidate.End, candidate.Quote, candidate.Type, candidate.Kind, request.operation_id.ToString(), "local-process-v1", modelVersion)); }
        catch (Exception ex) { errors.Add(new { candidate = candidate.Index, error = ex.Message }); }
    }
    return new { operation_id = request.operation_id, adapter_version = "local-process-v1", model_version = modelVersion, source_id = sourceId, source_text_sha256 = record.ExtractedTextSha256, created, possible_duplicates = duplicates, errors, adapter_stderr = Encoding.UTF8.GetString(stderr) };
}

static object CreateManualDraft(KnowledgeBaseStore store, Guid sourceId, string title, string body, string type, string kind, int startLine, int endLine, string quote, CliSettings cliSettings)
{
    var record = store.DraftCandidate(sourceId, title, body, startLine, endLine, quote, type, kind);
    var editor = cliSettings.PreferredEditor;
    if (string.IsNullOrWhiteSpace(editor)) return new { record, path = record.Path, message = "Set VISUAL or EDITOR to edit this draft" };
    var originalHash = KnowledgeRepository.FileHash(File.ReadAllText(record.Path)); using var temp = new TemporaryEditFile(record.Path); var commandParts = editor.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    var start = new System.Diagnostics.ProcessStartInfo(commandParts[0]) { UseShellExecute = false };
    if (commandParts.Length > 1) foreach (var argument in commandParts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(argument);
    start.ArgumentList.Add(temp.Path); using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException($"Editor exited with code {process.ExitCode}");
    try { return store.EditRecord(record.Metadata.Id, File.ReadAllText(temp.Path), originalHash); }
    catch (IOException) { temp.Preserve(); throw; }
}

static string RequiredString(JsonElement element, string property)
{
    if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException($"Candidate {property} is required");
    return value.GetString()!;
}
static string OptionalString(JsonElement element, string property, string fallback) => !element.TryGetProperty(property, out var value) ? fallback : value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException($"Candidate {property} must be a string");
sealed class TemporaryEditFile : IDisposable
{
    public string Path { get; }
    private bool preserve;
    public TemporaryEditFile(string source)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kb-edit-" + Guid.NewGuid().ToString("N") + ".md");
        File.Copy(source, Path);
    }
    public void Preserve() => preserve = true;
    public void Dispose() { if (!preserve && File.Exists(Path)) File.Delete(Path); }
}
