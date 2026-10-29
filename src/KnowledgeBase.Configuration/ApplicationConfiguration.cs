using System.Net;
using Microsoft.Extensions.Configuration;

namespace KnowledgeBase.Configuration;

public static class ApplicationConfiguration
{
    public static IConfiguration Load()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kb", "appsettings.json");
        return Load(path);
    }

    internal static IConfiguration Load(string path)
    {
        var permissionWarning = GetPermissionWarning(path);
        if (permissionWarning is not null) Console.Error.WriteLine(permissionWarning);

        return new ConfigurationBuilder()
            .AddJsonFile(path, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    internal static string? GetPermissionWarning(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsWindows()) return null;
        var mode = File.GetUnixFileMode(path);
        var sharedAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & sharedAccess) == 0 ? null :
            $"Warning: '{path}' is accessible to group or other users; restrict it to the owner because it may contain secrets.";
    }
}

public sealed record ServerSettings(string DatabasePath, string StatePath, string ClientRegistryPath, int Port)
{
    public static ServerSettings Load(IConfiguration configuration)
    {
        var database = Required(configuration, "KB_DATABASE_PATH");
        var state = Path.GetFullPath(configuration["KB_SERVER_STATE_PATH"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kb", "server"));
        var registry = configuration["KB_CLIENT_REGISTRY_PATH"] ?? Path.Combine(Path.GetDirectoryName(state)!, "clients.json");
        var portText = configuration["KB_SERVER_PORT"];
        var port = string.IsNullOrWhiteSpace(portText) ? 8765 : int.TryParse(portText, out var parsed) ? parsed : 0;
        if (port is < 1 or > 65535) throw new InvalidOperationException("KB_SERVER_PORT must be an integer between 1 and 65535.");
        return new(database, state, registry, port);
    }

    private static string Required(IConfiguration configuration, string key) =>
        !string.IsNullOrWhiteSpace(configuration[key]) ? configuration[key]! : throw new InvalidOperationException($"{key} is required in ~/.kb/appsettings.json or the environment.");
}

public sealed record McpSettings(Uri GatewayUrl, string Token, bool EnableDrafts, int TimeoutSeconds)
{
    public static McpSettings Load(IConfiguration configuration)
    {
        var rawUrl = configuration["KB_MCP_GATEWAY_URL"];
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(url.Host, out var address) || !IPAddress.IsLoopback(address) ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || url.AbsolutePath is not ("/" or ""))
            throw new InvalidOperationException("KB_MCP_GATEWAY_URL must be an HTTP URL with a numeric loopback address and no path, query, fragment, or userinfo.");
        var token = configuration["KB_MCP_GATEWAY_TOKEN"];
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("KB_MCP_GATEWAY_TOKEN is required.");
        var timeoutText = configuration["KB_MCP_HTTP_TIMEOUT_SECONDS"];
        var timeout = string.IsNullOrWhiteSpace(timeoutText) ? 15 : int.TryParse(timeoutText, out var parsed) ? parsed : 0;
        if (timeout is < 1 or > 300) throw new InvalidOperationException("KB_MCP_HTTP_TIMEOUT_SECONDS must be an integer between 1 and 300.");
        var draftsText = configuration["KB_MCP_ENABLE_DRAFTS"];
        if (!string.IsNullOrWhiteSpace(draftsText) && !bool.TryParse(draftsText, out _)) throw new InvalidOperationException("KB_MCP_ENABLE_DRAFTS must be true or false.");
        return new(url, token, string.Equals(draftsText, "true", StringComparison.OrdinalIgnoreCase), timeout);
    }
}

public sealed record CliSettings(string DefaultDatabasePath, string? ClientRegistryPath, string? ServerStatePath, string? Visual, string? Editor)
{
    public static CliSettings Load(IConfiguration configuration) => new(
        configuration["KB_HOME"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".knowledge-base"),
        configuration["KB_CLIENT_REGISTRY_PATH"], configuration["KB_SERVER_STATE_PATH"], configuration["VISUAL"], configuration["EDITOR"]);

    public string ResolveClientRegistryPath()
    {
        if (!string.IsNullOrWhiteSpace(ClientRegistryPath)) return ClientRegistryPath;
        var state = Path.GetFullPath(ServerStatePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kb", "server"));
        return Path.Combine(Path.GetDirectoryName(state)!, "clients.json");
    }

    public string? PreferredEditor => Visual ?? Editor;
}

public sealed record LocalExtractorSettings(string? Executable, string? ArgumentsJson, string? ModelVersion)
{
    public static LocalExtractorSettings Load(IConfiguration configuration) => new(
        configuration["KB_LOCAL_EXTRACTOR_EXECUTABLE"], configuration["KB_LOCAL_EXTRACTOR_ARGUMENTS"], configuration["KB_LOCAL_EXTRACTOR_MODEL_VERSION"]);
}
