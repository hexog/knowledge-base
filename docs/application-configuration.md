# Application configuration

The CLI, HTTP gateway, and MCP adapter read shared settings from `~/.kb/appsettings.json` and then load environment variables. Environment values override JSON values. CLI options such as `--db` override both. The file is optional; existing environment-based launch scripts continue to work.

Use the existing environment variable names as JSON property names. For example:

```json
{
  "KB_HOME": "/home/alex/knowledge-base",
  "KB_DATABASE_PATH": "/home/alex/knowledge-base",
  "KB_SERVER_STATE_PATH": "/home/alex/.kb/server",
  "KB_CLIENT_REGISTRY_PATH": "/home/alex/.kb/clients.json",
  "KB_SERVER_PORT": "8765",
  "KB_MCP_GATEWAY_URL": "http://127.0.0.1:8765",
  "KB_MCP_GATEWAY_TOKEN": "replace-with-your-token",
  "KB_MCP_HTTP_TIMEOUT_SECONDS": "15",
  "KB_MCP_ENABLE_DRAFTS": "false",
  "KB_LOCAL_EXTRACTOR_EXECUTABLE": "/path/to/extractor",
  "KB_LOCAL_EXTRACTOR_ARGUMENTS": "[\"--stdio\",\"--format\",\"json\"]",
  "KB_LOCAL_EXTRACTOR_MODEL_VERSION": "model-or-adapter-version",
  "EDITOR": "vim",
  "VISUAL": "vim"
}
```

The JSON file may contain a gateway token. Keep it outside the repository and restrict read access to the account that runs the applications (for example, `chmod 600 ~/.kb/appsettings.json` on Unix-like systems). Prefer the environment or the operating system's protected secret store when that better fits the deployment.

`KB_HOME` is the CLI's default database path. `KB_DATABASE_PATH` is required by the server. If unset, server state defaults to `~/.kb/server`, and the client registry defaults to `clients.json` beside the state directory. The server listens on loopback port `8765` unless `KB_SERVER_PORT` is set.

The MCP adapter requires a numeric loopback `KB_MCP_GATEWAY_URL` and `KB_MCP_GATEWAY_TOKEN`. Its timeout defaults to 15 seconds; drafts default to disabled. Extractor settings are only used by `kb extract`. `VISUAL` takes precedence over `EDITOR` when both are set.

The portable database has a separate `<database>/config/settings.json` for schema version and import-size limits. Do not put gateway credentials or process configuration in that file; it moves with the database.
