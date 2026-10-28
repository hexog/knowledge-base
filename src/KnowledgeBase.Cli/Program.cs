using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    new KnowledgeBaseStore(path).Initialize(); return new { path = Path.GetFullPath(path), schema_version = "1" };
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
var search = new Command("search", "Search indexed sources");
search.Arguments.Add(query); search.Options.Add(typeOption); search.Options.Add(statusOption); search.Options.Add(fromOption); search.Options.Add(toOption); search.Options.Add(limitOption);
search.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Search(parse.GetValue(query)!, parse.GetValue(limitOption),
    parse.GetValue(typeOption), ParseDate(parse.GetValue(fromOption), "--from", false), ParseDate(parse.GetValue(toOption), "--to", true), parse.GetValue(statusOption)),
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

root.Subcommands.Add(init); root.Subcommands.Add(import); root.Subcommands.Add(search); root.Subcommands.Add(show); root.Subcommands.Add(status); root.Subcommands.Add(rebuild);
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
        return result is ImportSummary summary && (summary.Errors > 0 || summary.Oversized > 0) ? 1 : 0;
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
        case ValueTuple<int, int> rebuilt: Console.WriteLine($"Index rebuilt: {rebuilt.Item1} indexed, {rebuilt.Item2} skipped."); break;
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
