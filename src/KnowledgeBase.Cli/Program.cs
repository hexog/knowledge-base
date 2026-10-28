using System.Text.Json;
using KnowledgeBase.Core;

static int Usage()
{
    Console.Error.WriteLine("Usage: kb <init|import|search|show|status|rebuild-index> [arguments] [--db PATH] [--json]");
    return 2;
}

var argsList = args.ToList();
bool json = argsList.Remove("--json");
string db = Environment.GetEnvironmentVariable("KB_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".knowledge-base");
int dbIndex = argsList.IndexOf("--db");
if (dbIndex >= 0) { if (dbIndex + 1 >= argsList.Count) return Usage(); db = argsList[dbIndex + 1]; argsList.RemoveRange(dbIndex, 2); }
if (argsList.Count == 0) return Usage();
var store = new KnowledgeBaseStore(db);
try
{
    object result;
    switch (argsList[0])
    {
        case "init": if (argsList.Count > 1) db = argsList[1]; store = new KnowledgeBaseStore(db); store.Initialize(); result = new { path = Path.GetFullPath(db), schema_version = "1" }; break;
        case "import": if (argsList.Count < 2) return Usage(); result = store.Import(argsList[1]); break;
        case "search":
            if (argsList.Count < 2) return Usage();
            string? type = Option(argsList, "--type"), state = Option(argsList, "--status");
            DateTimeOffset? from = DateTimeOffset.TryParse(Option(argsList, "--from"), out var f) ? f : null;
            DateTimeOffset? to = DateTimeOffset.TryParse(Option(argsList, "--to"), out var t) ? t : null;
            int limit = int.TryParse(Option(argsList, "--limit"), out var l) ? l : 10;
            result = store.Search(argsList[1], limit, type, from, to, state); break;
        case "show":
            if (argsList.Count < 2 || !Guid.TryParse(argsList[1], out var id)) return Usage();
            var (record, dir) = store.Show(id);
            result = new { metadata = record, original_path = Path.Combine(dir, "original" + Path.GetExtension(record.OriginalName)), extracted_text_path = Path.Combine(dir, "extracted.txt"), text = argsList.Contains("--text") && File.Exists(Path.Combine(dir, "extracted.txt")) ? File.ReadAllText(Path.Combine(dir, "extracted.txt")) : null }; break;
        case "status": result = store.Status(); break;
        case "rebuild-index": result = store.Rebuild(); break;
        default: return Usage();
    }
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    if (result is ImportSummary summary && (summary.Errors > 0 || summary.Oversized > 0)) return 1;
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(json ? JsonSerializer.Serialize(new { error = ex.Message }) : $"Error: {ex.Message}"); return 1; }

static string? Option(List<string> values, string name) { var i = values.IndexOf(name); return i >= 0 && i + 1 < values.Count ? values[i + 1] : null; }
