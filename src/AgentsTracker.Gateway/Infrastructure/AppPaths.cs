namespace AgentsTracker.Gateway.Infrastructure;

/// <summary>
/// Папка данных шлюза: <c>%LOCALAPPDATA%\AgentsTracker</c>. Здесь лежат state.json,
/// mcp-gateway-<pid>.json, локальный конфиг с секретами и журнал аудита.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> Directory_ = new(() =>
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentsTracker");
        Directory.CreateDirectory(dir);
        return dir;
    });

    public static string DataDirectory => Directory_.Value;

    /// <summary>Локальный конфиг с токеном бота и списком пользователей — вне папки проекта.</summary>
    public static string LocalSettings => Path.Combine(DataDirectory, "appsettings.Local.json");
}
