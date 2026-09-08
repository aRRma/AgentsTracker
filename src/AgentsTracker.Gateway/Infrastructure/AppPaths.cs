namespace AgentsTracker.Gateway.Infrastructure;

/// <summary>
/// Папка данных шлюза: <c>%LOCALAPPDATA%\AgentsTracker</c> на Windows, <c>~/.local/share</c>
/// на Linux и macOS. Здесь лежат state.json, mcp-gateway-&lt;pid&gt;.json, локальный конфиг
/// с секретами и журнал аудита.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Ключ переноса папки данных: <c>Gateway:DataDirectory</c>, в окружении
    /// <c>Gateway__DataDirectory</c>. Читается в обход обычного конфига — от этой папки
    /// зависит путь к самому файлу настроек.
    /// </summary>
    public const string SettingKey = "Gateway:DataDirectory";

    private static string? _override;

    private static readonly Lazy<string> Default = new(() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentsTracker"));

    public static string DataDirectory
    {
        get
        {
            var dir = _override ?? Default.Value;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Локальный конфиг с секретами — лежит вне папки проекта.</summary>
    public static string LocalSettings => Path.Combine(DataDirectory, "appsettings.Local.json");

    /// <summary>
    /// Переносит папку данных: в контейнере том монтируют в предсказуемое место
    /// (<c>/data</c>), а на одной машине могут жить два шлюза с разными ботами.
    /// Зовётся один раз до первого обращения к путям.
    /// </summary>
    public static void UseDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        _override = Path.GetFullPath(directory);
    }

    /// <summary>
    /// Берёт путь из appsettings.json рядом с exe и из окружения. Локальный конфиг здесь
    /// не читаем намеренно: он лежит в искомой папке, получился бы круг.
    /// </summary>
    public static void UseConfiguredDirectory()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();

        if (configuration[SettingKey] is { Length: > 0 } configured) UseDirectory(configured);
    }
}
