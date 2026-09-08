using System.Diagnostics;
using System.Text;

namespace AgentsTracker.Agents.Cursor;

/// <summary>
/// Ищет <c>agent</c>: путь из конфига → штатная установка → PATH.
/// Без этого шлюз стартовал бы и падал на первом сообщении стектрейсом.
/// </summary>
public sealed class CursorCliLocator(IOptions<CursorOptions> options, ILogger<CursorCliLocator> logger)
{
    private readonly CursorOptions _options = options.Value;
    private string? _resolved;

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public string Resolve()
    {
        if (_resolved is not null && File.Exists(_resolved)) return _resolved;

        if (_resolved is not null)
            logger.LogWarning("Cursor CLI пропал из {Path} — ищу заново", _resolved);

        _resolved = Locate() ?? throw new InvalidOperationException(
            "Не найден agent. Установите Cursor CLI командой  irm 'https://cursor.com/install?win32=true' | iex  "
            + "или укажите путь в Gateway:Cursor:Executable.");

        logger.LogInformation("Cursor CLI: {Path}", _resolved);
        return _resolved;
    }

    public string? TryGetVersion()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Resolve(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("--version");

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;

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
            logger.LogDebug(ex, "Не удалось спросить версию у {Path}", _resolved);
            return null;
        }
    }

    private string? Locate()
    {
        if (_options.Executable is { Length: > 0 } configured)
        {
            if (File.Exists(configured)) return configured;
            logger.LogWarning("Gateway:Cursor:Executable указывает на несуществующий файл: {Path}", configured);
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate)) return candidate;
        }

        return FindOnPath();
    }

    private static string[] ExecutableNames => OperatingSystem.IsWindows()
        ? ["agent.exe", "agent.cmd", "agent.bat"]
        : ["agent"];

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var name in ExecutableNames)
        {
            yield return Path.Combine(home, ".local", "bin", name);
            yield return Path.Combine(localApp, "cursor-agent", name);
            yield return Path.Combine(localApp, "Programs", "cursor-agent", name);
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "npm", "agent.cmd");
        }
        else
        {
            yield return "/usr/local/bin/agent";
            yield return "/usr/bin/agent";
        }
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in ExecutableNames)
            {
                string full;
                try { full = Path.Combine(dir, name); }
                catch (ArgumentException) { continue; }

                if (File.Exists(full)) return full;
            }
        }

        return null;
    }
}
