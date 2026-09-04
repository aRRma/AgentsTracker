using System.Diagnostics;

namespace AgentsTracker.Gateway.Infrastructure.Claude;

/// <summary>
/// Ищет исполняемый файл Claude Code: явный путь из конфига → стандартная установка → PATH →
/// бинарник, вложенный в расширение VS Code (последний вариант привязан к версии расширения
/// и исчезает при её обновлении, поэтому используется только как запасной).
/// </summary>
public sealed class ClaudeCliLocator(IOptions<GatewayOptions> options, ILogger<ClaudeCliLocator> logger)
{
    private readonly GatewayOptions _options = options.Value;
    private string? _resolved;

    /// <summary>Сколько ждать ответа на <c>claude --version</c>: это диагностика, а не работа.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public string Resolve()
    {
        // Найденный путь кешируем, но проверяем перед каждой выдачей: бинарник расширения VS Code
        // исчезает вместе со своей папкой при обновлении, и шлюз, живущий сутками, иначе до
        // перезапуска звал бы удалённый файл.
        if (_resolved is not null && File.Exists(_resolved)) return _resolved;

        if (_resolved is not null)
            logger.LogWarning("Claude CLI пропал из {Path} — ищу заново", _resolved);

        _resolved = Locate() ?? throw new InvalidOperationException(
            "Не найден claude.exe. Установите CLI командой  irm https://claude.ai/install.ps1 | iex  " +
            "или укажите путь в Gateway:ClaudeExecutable.");

        logger.LogInformation("Claude CLI: {Path}", _resolved);

        if (IsVsCodeExtensionBinary(_resolved))
        {
            logger.LogWarning(
                "Используется бинарник расширения VS Code. Он привязан к версии расширения и исчезнет "
                + "при его обновлении или удалении. Поставьте отдельный CLI:  irm https://claude.ai/install.ps1 | iex");
        }

        return _resolved;
    }

    /// <summary>
    /// Версия CLI для лога при старте. Контракт запуска (разбор JSON, форма ответа
    /// PermissionTool) держится на недокументированном поведении конкретной версии, поэтому
    /// при разборе жалоб первое, что нужно знать, — какой именно бинарник отвечал.
    /// </summary>
    public string? TryGetVersion()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Resolve(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit((int)VersionTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                return null;
            }

            return process.ExitCode == 0 && output.Trim() is { Length: > 0 } version ? version : null;
        }
        catch (Exception ex)
        {
            // Неизвестная версия — не повод не запускаться: сам запуск агента от неё не зависит.
            logger.LogDebug(ex, "Не удалось спросить версию у {Path}", _resolved);
            return null;
        }
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

        return FindOnPath() ?? VsCodeExtensionBinaries().FirstOrDefault(File.Exists);
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
    }

    /// <summary>Запасной вариант: бинарник внутри расширения VS Code, самая свежая версия.</summary>
    private static IEnumerable<string> VsCodeExtensionBinaries()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extensions = Path.Combine(home, ".vscode", "extensions");
        if (!Directory.Exists(extensions)) yield break;

        var dirs = Directory.EnumerateDirectories(extensions, "anthropic.claude-code-*")
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
            yield return Path.Combine(dir, "resources", "native-binary", "claude.exe");
    }

    private static bool IsVsCodeExtensionBinary(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}.vscode{Path.DirectorySeparatorChar}extensions{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

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
