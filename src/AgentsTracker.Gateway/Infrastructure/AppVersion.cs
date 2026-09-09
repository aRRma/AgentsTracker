using System.Reflection;

namespace AgentsTracker.Gateway.Infrastructure;

/// <summary>
/// Версия самого шлюза — та, что видна в логе, в мониторе и в сообщении о запуске.
/// Без неё по чату и по странице нельзя понять, какая сборка работает, а обновление
/// «распакуйте архив поверх» легко сделать наполовину.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Номер из <c>-p:Version</c> релизной сборки. Локальная сборка отдаёт
    /// <c>0.0.0-dev</c> из csproj: она не должна выглядеть выпущенной версией.
    /// </summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;

        // InformationalVersion — единственный, куда MSBuild кладёт суффикс вроде «-dev»;
        // хвост «+<коммит>» ставит SourceLink, человеку он не нужен.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is { Length: > 0 })
        {
            var plus = informational.IndexOf('+');
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "?";
    }
}
