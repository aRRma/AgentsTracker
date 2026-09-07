using Microsoft.Extensions.Configuration;

namespace AgentsTracker.Gateway.Infrastructure;

/// <summary>
/// Папка данных шлюза: <c>%LOCALAPPDATA%\AgentsTracker</c> на Windows, <c>~/.local/share</c>
/// на Linux и macOS. Здесь лежат state.json, mcp-gateway-&lt;pid&gt;.json, локальный конфиг
/// с секретами и журнал аудита.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Ключ, которым папку данных переносят: <c>Gateway:DataDirectory</c> и его вид для
    /// переменной окружения. Читается в обход обычной конфигурации — от этой папки зависит
    /// путь к самому файлу настроек.
    /// </summary>
    public const string SettingKey = "Gateway:DataDirectory";
    public const string EnvironmentKey = "Gateway__DataDirectory";

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

    /// <summary>Локальный конфиг с токеном бота и списком пользователей — вне папки проекта.</summary>
    public static string LocalSettings => Path.Combine(DataDirectory, "appsettings.Local.json");

    /// <summary>
    /// Переносит папку данных. Нужно в контейнере, где том монтируют в предсказуемое место
    /// (<c>/data</c>), и когда на одной машине живут два шлюза с разными ботами.
    /// Вызывается один раз до первого обращения к путям.
    /// </summary>
    public static void UseDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        _override = Path.GetFullPath(directory);
    }

    /// <summary>
    /// Забирает путь из appsettings.json рядом с exe и из переменных окружения. Локальный
    /// конфиг тут не читается намеренно: он сам лежит в искомой папке, и получился бы круг.
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
