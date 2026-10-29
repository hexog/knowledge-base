using KnowledgeBase.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);
var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kb", "appsettings.json");
var configuration = new ConfigurationBuilder().AddJsonFile(settingsPath, optional: true, reloadOnChange: false).AddEnvironmentVariables().Build();
var settings = McpSettings.Load(configuration);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var gateway = new GatewayClient(settings);
builder.Services.AddSingleton(gateway);
var concurrency = new ToolConcurrency();
var toolJson = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
var knowledgeTools = new KnowledgeTools(gateway, concurrency);
var availableTools = new List<McpServerTool>
{
    CreateTool(knowledgeTools.Search, "search", "Search knowledge records available to this gateway client.", """
        {"type":"object","properties":{"results":{"type":"array","items":{"type":"object","properties":{"id":{"type":"string","format":"uuid"},"version":{"type":"string"},"type":{"type":"string"},"kind":{"type":"string"},"status":{"type":"string"},"title":{"type":"string"},"snippet":{"type":"string"},"citations":{"type":"array","items":{"type":"object","properties":{"source_id":{"type":"string","format":"uuid"},"text_sha256":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"},"text":{"type":"string"}},"required":["source_id","text_sha256","start_line","end_line","text"],"additionalProperties":false}}},"required":["id","version","type","kind","status","title","snippet","citations"],"additionalProperties":false}},"cursor":{"type":["string","null"]},"policy_revision":{"type":"integer"}},"required":["results","cursor","policy_revision"],"additionalProperties":false}
        """, readOnly: true, idempotent: false, toolJson),
    CreateTool(knowledgeTools.GetRecord, "get_record", "Read one record allowed by the configured gateway client.", """
        {"type":"object","properties":{"id":{"type":"string","format":"uuid"},"version":{"type":"string"},"type":{"type":"string"},"kind":{"type":"string"},"status":{"type":"string"},"title":{"type":"string"},"body":{"type":"string"},"citations":{"type":"array","items":{"type":"object","properties":{"source_id":{"type":"string","format":"uuid"},"text_sha256":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"},"text":{"type":"string"}},"required":["source_id","text_sha256","start_line","end_line","text"],"additionalProperties":false}},"policy_revision":{"type":"integer"}},"required":["id","version","type","kind","status","title","body","citations","policy_revision"],"additionalProperties":false}
        """, readOnly: true, idempotent: false, toolJson),
    CreateTool(knowledgeTools.GetSourceText, "get_source_text", "Read an exact line range from an allowed source.", """
        {"type":"object","properties":{"source_id":{"type":"string","format":"uuid"},"name":{"type":"string"},"start_line":{"type":"integer"},"end_line":{"type":"integer"},"text":{"type":"string"},"policy_revision":{"type":"integer"}},"required":["source_id","name","start_line","end_line","text","policy_revision"],"additionalProperties":false}
        """, readOnly: true, idempotent: false, toolJson)
};
if (settings.EnableDrafts)
{
    var draftTools = new DraftTools(gateway, concurrency);
    availableTools.Add(CreateTool(draftTools.DraftRecord, "draft_record", "Create a knowledge draft; reuse idempotency_key after an unknown outcome.", """
        {"type":"object","properties":{"id":{"type":"string","format":"uuid"},"status":{"const":"draft"},"operation_id":{"type":"string"},"policy_revision":{"type":"integer"}},"required":["id","status","operation_id","policy_revision"],"additionalProperties":false}
        """, readOnly: false, idempotent: true, toolJson));
}
var mcp = builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(availableTools)
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        var arguments = context.Params.Arguments ?? new Dictionary<string, JsonElement>();
        var argumentBytes = JsonSerializer.SerializeToUtf8Bytes(arguments);
        if (argumentBytes.Length > 1024 * 1024)
            return McpResults.Error("request_too_large", false, false, null, null);
        var allowed = context.Params.Name switch
        {
            "search" => new[] { "query", "limit", "cursor" },
            "get_record" => new[] { "id" },
            "get_source_text" => new[] { "source_id", "start_line", "end_line" },
            "draft_record" => new[] { "title", "body", "citations", "idempotency_key" },
            _ => Array.Empty<string>()
        };
        if (arguments.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)))
            return McpResults.Error("invalid_request", false, false, null, null);
        return await next(context, cancellationToken);
    }));
builder.Services.Configure<McpServerOptions>(options => options.ServerInstructions = "Knowledge base tools use the configured local HTTP gateway.");
await builder.Build().RunAsync();

static McpServerTool CreateTool(Delegate handler, string name, string description, string dataSchemaJson, bool readOnly, bool idempotent, JsonSerializerOptions serializerOptions)
{
    var schemaJson = """
        {"type":"object","oneOf":[
          {"type":"object","properties":{"ok":{"const":true},"data":__DATA_SCHEMA__,"gateway_request_id":{"type":"string"}},"required":["ok","data"],"additionalProperties":false},
          {"type":"object","properties":{"ok":{"const":false},"error":{"type":"object","properties":{"code":{"type":"string"},"retryable":{"type":"boolean"},"outcome_unknown":{"type":"boolean"},"gateway_request_id":{"type":"string"},"idempotency_key":{"type":"string"}},"required":["code","retryable","outcome_unknown"],"additionalProperties":false}},"required":["ok","error"],"additionalProperties":false}
        ]}
        """.Replace("__DATA_SCHEMA__", dataSchemaJson, StringComparison.Ordinal);
    using var schema = JsonDocument.Parse(schemaJson);
    return McpServerTool.Create(handler, new McpServerToolCreateOptions
    {
        Name = name,
        Description = description,
        ReadOnly = readOnly,
        Destructive = false,
        Idempotent = idempotent,
        UseStructuredContent = true,
        OutputSchema = schema.RootElement.Clone(),
        SerializerOptions = serializerOptions,
        SchemaCreateOptions = new AIJsonSchemaCreateOptions
        {
            TransformOptions = new AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true }
        }
    });
}
