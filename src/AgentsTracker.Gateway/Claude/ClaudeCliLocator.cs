using AgentsTracker.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace AgentsTracker.Gateway.Claude;

/// <summary>
/// Ищет исполняемый файл Claude Code: явный путь из конфига → PATH → стандартная установка →
/// бинарник, вложенный в расширение VS Code (последний вариант привязан к версии расширения
/// и меняется при обновлении, поэтому используется только как запасной).
/// </summary>
public sealed class ClaudeCliLocator(IOptions<GatewayOptions> options, ILogger<ClaudeCliLocator> logger)
{
    private readonly GatewayOptions _options = options.Value;
    private string? _resolved;

    public string Resolve()
    {
        if (_resolved is not null) return _resolved;

        _resolved = Locate() ?? throw new InvalidOperationException(
            "Не найден claude.exe. Установите CLI командой  irm https://claude.ai/install.ps1 | iex  " +
            "или укажите путь в Gateway:ClaudeExecutable.");

        logger.LogInformation("Claude CLI: {Path}", _resolved);
        return _resolved;
    }

    private string? Locate()
    {
        if (_options.ClaudeExecutable is { Length: > 0 } configured)
        {
            if (File.Exists(configured)) return configured;
            logger.LogWarning("Gateway:ClaudeExecutable указывает на несуществующий файл: {Path}", configured);
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath();
    }

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Штатная установка native-инсталлятором.
        yield return Path.Combine(home, ".local", "bin", "claude.exe");
        yield return Path.Combine(home, ".claude", "local", "claude.exe");

        // Глобальная установка через npm.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "npm", "claude.cmd");

        // Запасной вариант: бинарник внутри расширения VS Code. Берём самую свежую версию.
        var extensions = Path.Combine(home, ".vscode", "extensions");
        if (!Directory.Exists(extensions)) yield break;

        var dirs = Directory.EnumerateDirectories(extensions, "anthropic.claude-code-*")
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
            yield return Path.Combine(dir, "resources", "native-binary", "claude.exe");
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;

        string[] names = ["claude.exe", "claude.cmd", "claude.bat"];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                string full;
                try { full = Path.Combine(dir, name); }
                catch (ArgumentException) { continue; } // недопустимые символы в элементе PATH

                if (File.Exists(full)) return full;
            }
        }

        return null;
    }
}
