# OpenCode MCP pilot

`KnowledgeBase.Mcp` is a local stdio MCP process. It reaches the knowledge base only through the loopback HTTP gateway. The pinned MCP C# SDK is [`ModelContextProtocol` 2.3.0](https://www.nuget.org/packages/ModelContextProtocol/2.3.0), using the SDK's [stdio server hosting](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md).

## Start the gateway

Follow [Agent gateway MVP](agent-gateway.md) to migrate the database, start the gateway, and issue a client credential. For the first pilot, use an `external` credential with `search,read` scopes. Keep drafts disabled until the read-only pilot is complete. A draft-enabled run needs an explicitly enabled adapter and a separate credential with the `draft` scope.

Set the adapter environment in the shell or the operating system's protected environment store. Do not put the token itself in an OpenCode project configuration file, command-line argument, or repository file:

```sh
export KB_MCP_GATEWAY_URL=http://127.0.0.1:8765
export KB_MCP_ENABLE_DRAFTS=false
export KB_MCP_HTTP_TIMEOUT_SECONDS=15
read -rsp 'Gateway token: ' KB_MCP_GATEWAY_TOKEN
printf '\n'
export KB_MCP_GATEWAY_TOKEN
```

Build the solution, then use the absolute path to the built DLL. For example, from the repository root in a Debug build:

```sh
dotnet build KnowledgeBase.slnx
```

Add this server to the OpenCode v2 configuration for the pilot project. Replace `/absolute/path/to/mc-space` with the repository checkout path. The `{env:...}` expression asks OpenCode to pass the variable from its own environment; it does not store the token value in this file.

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "knowledge-base": {
        "type": "local",
        "command": [
          "dotnet",
          "/absolute/path/to/mc-space/src/KnowledgeBase.Mcp/bin/Debug/net10.0/KnowledgeBase.Mcp.dll"
        ],
        "cwd": "/absolute/path/to/mc-space",
        "environment": {
          "KB_MCP_GATEWAY_URL": "{env:KB_MCP_GATEWAY_URL}",
          "KB_MCP_GATEWAY_TOKEN": "{env:KB_MCP_GATEWAY_TOKEN}",
          "KB_MCP_ENABLE_DRAFTS": "false",
          "KB_MCP_HTTP_TIMEOUT_SECONDS": "15"
        }
      }
    }
  }
}
```

OpenCode v2 uses named servers under [`mcp.servers`](https://dev.opencode.ai/v2/docs/mcp-servers/), a local `command` array, and `environment` substitutions. Check the connection with `opencode mcp list` or `/mcps`. Do not infer compatibility from the SDK process test: the manual OpenCode launch has not been completed in this environment because the installed OpenCode binary cannot start without FUSE.

## Enable drafts

Only after the read pilot, issue a separate gateway credential that includes `draft`, set `KB_MCP_ENABLE_DRAFTS=true`, and restart the MCP process. The client should then see `draft_record`. Each draft call must include an `idempotency_key`; on a timeout, cancellation after dispatch, or other unknown outcome, retry the same arguments with the same key. Drafts remain drafts for owner review.

## Limits and operational notes

- The adapter accepts only an HTTP URL with a numeric loopback IP and no path, query, fragment, or userinfo.
- It disables HTTP proxying and redirects, uses only fixed `/v1/...` routes, allows four concurrent tool calls, and uses a 15 second default HTTP timeout.
- Request arguments are bounded to 1 MiB, gateway responses to 8 MiB, and complete tool results to 256 KiB. Responses are rejected whole when they exceed a limit.
- The adapter does not cache results or retry gateway requests automatically. A lost search cursor should be replaced by a new search with `cursor=null`.
- MCP protocol output uses stdout. Diagnostics use stderr and omit gateway payloads and credentials.
- Revoke the pilot credential with `kb client revoke CLIENT_ID`; already delivered text cannot be recalled.

Use a separate OpenCode project directory and a small, explicitly approved corpus for the real model pilot. This MCP process does not isolate OpenCode from local files or other tools available to the agent.

Record actual client runs in [the pilot results template](mcp-pilot-results.md). Leave unavailable scenarios marked pending; do not fill them with synthetic IDs or citations.
