using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KnowledgeBase.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace KnowledgeBase.Tests;

public sealed class McpProcessTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "kb-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string database;
    private readonly string serverState;
    private readonly string registry;
    private readonly string adapterDll;
    private GatewayProcess? gateway;

    public McpProcessTests()
    {
        Directory.CreateDirectory(temp);
        database = Path.Combine(temp, "database");
        serverState = Path.Combine(temp, "server-state");
        registry = Path.Combine(temp, "clients.json");
        adapterDll = Path.Combine(AppContext.BaseDirectory, "KnowledgeBase.Mcp.dll");
    }

    [Fact]
    public async Task ProcessUsesGatewayAndOnlyRegistersDraftWhenEnabled()
    {
        var store = new KnowledgeBaseStore(database);
        store.Initialize();
        var sourcePath = Path.Combine(temp, "evidence.txt");
        File.WriteAllText(sourcePath, "MCP public evidence citation.\nSecond line.");
        var sourceId = store.Import(sourcePath).Items.Single().SourceId!.Value;
        store.SetSourceAccess(sourceId, "external_allowed", "MCP integration test");
        var record = store.CreateAgentDraft("MCP integration record", "MCP public evidence is available through the gateway.", [(sourceId, 1, 1, "MCP public evidence citation.")]);
        store.SetRecordAccessOverride(record.Metadata.Id, "external_allowed", "MCP integration test");
        store.AcceptRecord(record.Metadata.Id, [sourceId]);
        store.Migrate(2);
        store.Migrate(3);

        var issued = new ClientRegistry(registry).Create("external", ["search", "read", "draft"]);
        gateway = new GatewayProcess(database, serverState, registry);

        var stderr = new StringBuilder();
        await using (var client = await ConnectMcp(issued.Token, enabledDrafts: false, stderr))
        {
            var tools = await client.ListToolsAsync();
            Assert.Equal(new[] { "get_record", "get_source_text", "search" }, tools.Select(x => x.Name).OrderBy(x => x).ToArray());
            foreach (var tool in tools)
            {
                using var inputSchema = JsonDocument.Parse(JsonSerializer.Serialize(tool.JsonSchema));
                Assert.Equal(JsonValueKind.False, inputSchema.RootElement.GetProperty("additionalProperties").ValueKind);
                Assert.NotNull(tool.ReturnJsonSchema);
            }
            var auditPath = Path.Combine(serverState, "agent-audit.jsonl");
            long? auditLength = File.Exists(auditPath) ? new FileInfo(auditPath).Length : null;
            await Assert.ThrowsAnyAsync<Exception>(async () => await client.CallToolAsync("draft_record", new Dictionary<string, object?>()));
            long? auditLengthAfter = File.Exists(auditPath) ? new FileInfo(auditPath).Length : null;
            Assert.Equal(auditLength, auditLengthAfter);
            var rejectedExtra = await client.CallToolAsync("search", new Dictionary<string, object?>
            {
                ["query"] = "public evidence",
                ["unexpected"] = "must not reach the gateway"
            });
            Assert.True(rejectedExtra.IsError);
            Assert.Contains("invalid_request", JsonSerializer.Serialize(rejectedExtra.StructuredContent));
            var result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "public evidence" });
            Assert.NotEqual(true, result.IsError);
            Assert.NotNull(result.StructuredContent);
            var json = JsonSerializer.Serialize(result.StructuredContent);
            Assert.Contains("MCP public evidence citation", json);
            Assert.DoesNotContain(issued.Token, json);
            Assert.Contains("gateway_request_id", json);
            Assert.Single(result.Content);
            Assert.Equal(json, Assert.IsType<TextContentBlock>(result.Content[0]).Text);

            var source = await client.CallToolAsync("get_source_text", new Dictionary<string, object?>
            {
                ["source_id"] = sourceId,
                ["start_line"] = 1,
                ["end_line"] = 1
            });
            Assert.True(source.IsError != true, JsonSerializer.Serialize(source.Content));
            Assert.Contains("MCP public evidence citation", JsonSerializer.Serialize(source.StructuredContent));
            var readRecord = await client.CallToolAsync("get_record", new Dictionary<string, object?> { ["id"] = record.Metadata.Id });
            Assert.Contains(record.Metadata.Id.ToString(), JsonSerializer.Serialize(readRecord.StructuredContent));
            var unavailable = await client.CallToolAsync("get_record", new Dictionary<string, object?> { ["id"] = Guid.NewGuid() });
            Assert.True(unavailable.IsError);
            Assert.Contains("resource_unavailable", JsonSerializer.Serialize(unavailable.StructuredContent));
        }

        var searchOnly = new ClientRegistry(registry).Create("external", ["search"]);
        await using (var client = await ConnectMcp(searchOnly.Token, enabledDrafts: false, stderr))
        {
            var denied = await client.CallToolAsync("get_source_text", new Dictionary<string, object?>
            {
                ["source_id"] = sourceId,
                ["start_line"] = 1,
                ["end_line"] = 1
            });
            Assert.True(denied.IsError);
            Assert.Contains("forbidden", JsonSerializer.Serialize(denied.StructuredContent));
        }

        string firstDraftData;
        await using (var client = await ConnectMcp(issued.Token, enabledDrafts: true, stderr))
        {
            var tools = await client.ListToolsAsync();
            Assert.Contains(tools, x => x.Name == "draft_record");
            using var draftSchema = JsonDocument.Parse(JsonSerializer.Serialize(tools.Single(x => x.Name == "draft_record").JsonSchema));
            Assert.Equal(JsonValueKind.False, draftSchema.RootElement.GetProperty("additionalProperties").ValueKind);
            var citationSchema = draftSchema.RootElement.GetProperty("properties").GetProperty("citations").GetProperty("items");
            Assert.Equal(JsonValueKind.False, citationSchema.GetProperty("additionalProperties").ValueKind);
            var draft = await client.CallToolAsync("draft_record", new Dictionary<string, object?>
            {
                ["title"] = "MCP draft",
                ["body"] = "A draft over the gateway.",
                ["idempotency_key"] = "mcp-process-test-1",
                ["citations"] = new[] { new { source_id = sourceId, start_line = 1, end_line = 1, quote = "MCP public evidence citation." } }
            });
            Assert.NotEqual(true, draft.IsError);
            using var receipt = JsonDocument.Parse(JsonSerializer.Serialize(draft.StructuredContent));
            Assert.Equal("draft", receipt.RootElement.GetProperty("data").GetProperty("status").GetString());
            firstDraftData = receipt.RootElement.GetProperty("data").GetRawText();
        }

        await using (var restartedClient = await ConnectMcp(issued.Token, enabledDrafts: true, stderr))
        {
            var replay = await restartedClient.CallToolAsync("draft_record", new Dictionary<string, object?>
            {
                ["title"] = "MCP draft",
                ["body"] = "A draft over the gateway.",
                ["idempotency_key"] = "mcp-process-test-1",
                ["citations"] = new[] { new { source_id = sourceId, start_line = 1, end_line = 1, quote = "MCP public evidence citation." } }
            });
            using var replayReceipt = JsonDocument.Parse(JsonSerializer.Serialize(replay.StructuredContent));
            Assert.Equal(firstDraftData, replayReceipt.RootElement.GetProperty("data").GetRawText());
            var keyConflict = await restartedClient.CallToolAsync("draft_record", new Dictionary<string, object?>
            {
                ["title"] = "MCP draft",
                ["body"] = "A different draft body.",
                ["idempotency_key"] = "mcp-process-test-1",
                ["citations"] = new[] { new { source_id = sourceId, start_line = 1, end_line = 1, quote = "MCP public evidence citation." } }
            });
            Assert.True(keyConflict.IsError);
            Assert.Contains("idempotency_conflict", JsonSerializer.Serialize(keyConflict.StructuredContent));
        }

        new ClientRegistry(registry).Revoke(issued.ClientId);
        await using (var revokedClient = await ConnectMcp(issued.Token, enabledDrafts: false, stderr))
        {
            var unauthorized = await revokedClient.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "public evidence" });
            Assert.True(unauthorized.IsError);
            Assert.Contains("unauthorized", JsonSerializer.Serialize(unauthorized.StructuredContent));
            Assert.Contains("gateway_request_id", JsonSerializer.Serialize(unauthorized.StructuredContent));
        }

        Assert.DoesNotContain(issued.Token, stderr.ToString());
        Assert.DoesNotContain("MCP public evidence citation", stderr.ToString());
        Assert.DoesNotContain("mcp-process-test-1", stderr.ToString());
        Assert.DoesNotContain("A draft over the gateway.", stderr.ToString());
        Assert.DoesNotContain("public evidence", stderr.ToString());
    }

    private async Task<McpClient> ConnectMcp(string token, bool enabledDrafts, StringBuilder stderr)
    {
        var options = new StdioClientTransportOptions
        {
            Name = "KnowledgeBase.Mcp test",
            Command = "dotnet",
            Arguments = [adapterDll],
            WorkingDirectory = temp,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["KB_MCP_GATEWAY_URL"] = gateway!.BaseAddress.ToString().TrimEnd('/'),
                ["KB_MCP_GATEWAY_TOKEN"] = token,
                ["KB_MCP_ENABLE_DRAFTS"] = enabledDrafts ? "true" : "false"
            },
            StandardErrorLines = line => { lock (stderr) stderr.AppendLine(line); }
        };
        return await McpClient.CreateAsync(new StdioClientTransport(options));
    }

    public void Dispose()
    {
        gateway?.Dispose();
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
    }

    private sealed class GatewayProcess : IDisposable
    {
        private readonly string database;
        private readonly string state;
        private readonly string registry;
        private readonly int port;
        private readonly Process process;
        public Uri BaseAddress { get; }

        public GatewayProcess(string database, string state, string registry)
        {
            this.database = database;
            this.state = state;
            this.registry = registry;
            port = ReservePort();
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            var root = FindRepositoryRoot();
            var output = new DirectoryInfo(AppContext.BaseDirectory);
            var serverDll = Path.Combine(root, "src", "KnowledgeBase.Server", "bin", output.Parent!.Name, output.Name, "KnowledgeBase.Server.dll");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add(serverDll);
            start.Environment["KB_DATABASE_PATH"] = this.database;
            start.Environment["KB_SERVER_STATE_PATH"] = this.state;
            start.Environment["KB_CLIENT_REGISTRY_PATH"] = this.registry;
            start.Environment["KB_SERVER_PORT"] = port.ToString();
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the gateway process.");
            var error = new StringBuilder();
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) lock (error) error.AppendLine(args.Data); };
            process.BeginErrorReadLine();
            try
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (process.HasExited) throw new InvalidOperationException($"Gateway exited at startup: {error}");
                    using var socket = new TcpClient(AddressFamily.InterNetwork);
                    using var connectTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    try
                    {
                        socket.ConnectAsync(IPAddress.Loopback, port, connectTimeout.Token).GetAwaiter().GetResult();
                        return;
                    }
                    catch (SocketException) { }
                    catch (OperationCanceledException) { }
                    Thread.Sleep(50);
                }
                throw new TimeoutException($"Gateway did not start. Process id: {process.Id}; exited: {process.HasExited}; stderr: {error}");
            }
            catch { Stop(); throw; }
        }

        public void Dispose()
        {
            Stop();
        }

        private void Stop()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            finally { process.Dispose(); }
        }

        private static int ReservePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        private static string FindRepositoryRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "KnowledgeBase.slnx"))) return dir.FullName;
            throw new DirectoryNotFoundException("Repository root was not found.");
        }
    }
}
