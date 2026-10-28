using System.Text.Json;

var mode = args.Single();
if (mode == "no_read") return;
if (mode == "no_read_wait" || mode == "wait_forever") { await Task.Delay(Timeout.InfiniteTimeSpan); return; }
var request = await Console.In.ReadToEndAsync();
if (mode == "over_stdout") { Console.Out.Write(new string('x', 4 * 1024 * 1024 + 1024)); return; }
if (mode == "over_stderr") { Console.Error.Write(new string('x', 64 * 1024 + 1024)); return; }
if (mode == "exit_error") { Console.Error.Write("controlled child failure"); Environment.ExitCode = 7; return; }
if (mode == "malformed_json") { Console.Out.Write("not-json"); return; }
if (mode == "hang_after_read") { await Task.Delay(Timeout.InfiniteTimeSpan); return; }
if (mode == "valid_mixed")
{
    using var doc = JsonDocument.Parse(request);
    var text = doc.RootElement.GetProperty("text").GetString()!;
    var quote = text.Split('\n')[0];
    Console.Out.Write(JsonSerializer.Serialize(new
    {
        schema_version = "1",
        candidates = new object[]
        {
            new { title = "Valid extracted claim", body = "A concise claim.", type = "fact", kind = "source_claim", start_line = 1, end_line = 1, quote },
            new { title = "Invalid extracted claim", body = "Bad citation.", type = "fact", kind = "source_claim", start_line = 1, end_line = 1, quote = "wrong quote" },
            new { title = "Valid extracted claim", body = "A concise claim.", type = "fact", kind = "source_claim", start_line = 1, end_line = 1, quote }
        }
    }));
    return;
}
throw new ArgumentException($"Unknown mode: {mode}");
