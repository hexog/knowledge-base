using Microsoft.Extensions.Configuration;

namespace KnowledgeBase.Core;

public static class ApplicationConfiguration
{
    public static IConfiguration Load()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kb", "appsettings.json");
        return Load(path);
    }

    internal static IConfiguration Load(string path)
    {
        return new ConfigurationBuilder()
            .AddJsonFile(path, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }
}
