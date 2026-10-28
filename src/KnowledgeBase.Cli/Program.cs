using System.CommandLine;
using System.Text.Json;
using KnowledgeBase.Core;

var defaultDatabase = Environment.GetEnvironmentVariable("KB_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".knowledge-base");

var databaseOption = new Option<string>("--db") { Description = "Knowledge-base directory", DefaultValueFactory = _ => defaultDatabase };
var jsonOption = new Option<bool>("--json") { Description = "Write errors as JSON" };
var root = new RootCommand("Local personal knowledge base");
root.Options.Add(databaseOption);
root.Options.Add(jsonOption);

var initPath = new Argument<string?>("path") { Description = "Knowledge-base directory" };
var init = new Command("init", "Create a knowledge base");
init.Arguments.Add(initPath);
init.SetAction(parse => Run(() =>
{
    var path = parse.GetValue(initPath) ?? parse.GetValue(databaseOption)!;
    new KnowledgeBaseStore(path).Initialize();
    return new { path = Path.GetFullPath(path), schema_version = "1" };
}, parse.GetValue(jsonOption)));

var importPath = new Argument<string>("path") { Description = "File or directory to import" };
var import = new Command("import", "Import a file or directory");
import.Arguments.Add(importPath);
import.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Import(parse.GetValue(importPath)!), parse.GetValue(jsonOption)));

var query = new Argument<string>("query") { Description = "Full-text search query" };
var typeOption = new Option<string?>("--type") { Description = "Filter by source type" };
var statusOption = new Option<string?>("--status") { Description = "Filter by extraction status" };
var fromOption = new Option<string?>("--from") { Description = "Imported on or after this date" };
var toOption = new Option<string?>("--to") { Description = "Imported on or before this date" };
var limitOption = new Option<int>("--limit") { Description = "Maximum number of results", DefaultValueFactory = _ => 10 };
var search = new Command("search", "Search indexed sources");
search.Arguments.Add(query);
search.Options.Add(typeOption); search.Options.Add(statusOption); search.Options.Add(fromOption); search.Options.Add(toOption); search.Options.Add(limitOption);
search.SetAction(parse => Run(() =>
{
    var from = ParseDate(parse.GetValue(fromOption), "--from");
    var to = ParseDate(parse.GetValue(toOption), "--to");
    return new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Search(parse.GetValue(query)!, parse.GetValue(limitOption), parse.GetValue(typeOption), from, to, parse.GetValue(statusOption));
}, parse.GetValue(jsonOption)));

var sourceId = new Argument<Guid>("source-id") { Description = "Source UUID" };
var textOption = new Option<bool>("--text") { Description = "Include extracted text" };
var show = new Command("show", "Show a source record"); show.Arguments.Add(sourceId); show.Options.Add(textOption);
show.SetAction(parse => Run(() =>
{
    var store = new KnowledgeBaseStore(parse.GetValue(databaseOption)!);
    var (record, dir) = store.Show(parse.GetValue(sourceId));
    var textPath = Path.Combine(dir, "extracted.txt");
    return new { metadata = record, original_path = Path.Combine(dir, "original" + Path.GetExtension(record.OriginalName)), extracted_text_path = textPath, text = parse.GetValue(textOption) && File.Exists(textPath) ? File.ReadAllText(textPath) : null };
}, parse.GetValue(jsonOption)));

var status = new Command("status", "Show database status");
status.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Status(), parse.GetValue(jsonOption)));
var rebuild = new Command("rebuild-index", "Rebuild the full-text index");
rebuild.SetAction(parse => Run(() => new KnowledgeBaseStore(parse.GetValue(databaseOption)!).Rebuild(), parse.GetValue(jsonOption)));

root.Subcommands.Add(init); root.Subcommands.Add(import); root.Subcommands.Add(search);
root.Subcommands.Add(show); root.Subcommands.Add(status); root.Subcommands.Add(rebuild);

var parseResult = root.Parse(args);
if (parseResult.Errors.Count > 0)
{
    foreach (var error in parseResult.Errors) Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("Use 'kb --help' for command usage.");
    return 2;
}
return parseResult.Invoke();

static int Run<T>(Func<T> action, bool json)
{
    try
    {
        var result = action();
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return result is ImportSummary summary && (summary.Errors > 0 || summary.Oversized > 0) ? 1 : 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(json ? JsonSerializer.Serialize(new { error = ex.Message }) : $"Error: {ex.Message}");
        return 1;
    }
}

static DateTimeOffset? ParseDate(string? value, string option)
{
    if (value is null) return null;
    if (DateTimeOffset.TryParse(value, out var date)) return date;
    throw new ArgumentException($"Invalid date for {option}: {value}");
}
