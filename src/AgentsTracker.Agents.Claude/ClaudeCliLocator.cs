using System.Diagnostics;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Ищет claude: путь из конфига → штатная установка → PATH → бинарник внутри расширения
/// VS Code. Последний только на крайний случай: он привязан к версии расширения
/// и исчезает при обновлении.
/// </summary>
public sealed class ClaudeCliLocator(IOptions<ClaudeOptions> options, ILogger<ClaudeCliLocator> logger)
{
    private readonly ClaudeOptions _options = options.Value;
    private string? _resolved;

    /// <summary>Сколько ждать ответа на <c>claude --version</c>: это диагностика, а не работа.</summary>
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public string Resolve()
    {
        // Путь кешируем, но проверяем перед каждой выдачей: бинарник расширения VS Code
        // исчезает вместе со своей папкой, и шлюз, живущий сутками, звал бы удалённый файл
        // до перезапуска.
        if (_resolved is not null && File.Exists(_resolved)) return _resolved;

        if (_resolved is not null)
            logger.LogWarning("Claude CLI пропал из {Path} — ищу заново", _resolved);

        _resolved = Locate() ?? throw new InvalidOperationException(
            "Не найден claude.exe. Установите CLI командой  irm https://claude.ai/install.ps1 | iex  " +
            "или укажите путь в Gateway:Claude:Executable.");

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
    /// Версия CLI для лога при старте: контракт запуска держится на недокументированном
    /// поведении конкретной версии, и при разборе жалоб знать её нужно первым делом.
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

            // Оба потока читаем параллельно: с невычитанным stderr CLI упёрся бы в полный
            // буфер на длинном предупреждении, а мы — в ReadToEnd stdout.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)VersionTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                return null;
            }

            var output = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();

            return process.ExitCode == 0 && output.Trim() is { Length: > 0 } version ? version : null;
        }
        catch (Exception ex)
        {
            // Неизвестная версия старту не мешает: запуск агента от неё не зависит.
            logger.LogDebug(ex, "Не удалось спросить версию у {Path}", _resolved);
            return null;
        }
    }

    private string? Locate()
    {
        if (_options.Executable is { Length: > 0 } configured)
        {
            if (File.Exists(configured)) return configured;
            logger.LogWarning("Gateway:Claude:Executable указывает на несуществующий файл: {Path}", configured);
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath() ?? VsCodeExtensionBinaries().FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Имена файла: на Windows с расширением, на Linux и macOS без. Порядок важен — первым
    /// то, что ставит штатный инсталлятор.
    /// </summary>
    private static string[] ExecutableNames => OperatingSystem.IsWindows()
        ? ["claude.exe", "claude.cmd", "claude.bat"]
        : ["claude"];

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Штатная установка native-инсталлятором.
        foreach (var name in ExecutableNames)
        {
            yield return Path.Combine(home, ".local", "bin", name);
            yield return Path.Combine(home, ".claude", "local", name);
        }

        // Глобальный npm: на Windows обёртка .cmd в %APPDATA%\npm, иначе симлинк в общем bin.
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "npm", "claude.cmd");
        }
        else
        {
            yield return "/usr/local/bin/claude";
            yield return "/usr/bin/claude";
            yield return Path.Combine(home, ".npm-global", "bin", "claude");
        }
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
        {
            foreach (var name in ExecutableNames)
                yield return Path.Combine(dir, "resources", "native-binary", name);
        }
    }

    private static bool IsVsCodeExtensionBinary(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}.vscode{Path.DirectorySeparatorChar}extensions{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;

        var names = ExecutableNames;

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
