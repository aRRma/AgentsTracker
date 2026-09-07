using System.Diagnostics;
using System.Text;

namespace AgentsTracker.Gateway.Infrastructure.Autostart;

/// <summary>Выбор реализации автозапуска по текущей ОС.</summary>
public static class AutostartInstaller
{
    public static IAutostartInstaller ForCurrentOs() => OperatingSystem.IsWindows()
        ? new WindowsScheduledTaskInstaller()
        : new UnsupportedAutostartInstaller();
}

/// <summary>
/// Заглушка для систем, где автозапуск ещё не сделан. Не бросает при чтении состояния,
/// чтобы <c>uninstall</c> на чужой ОС отвечал «нечего снимать», а не стектрейсом.
/// </summary>
public sealed class UnsupportedAutostartInstaller : IAutostartInstaller
{
    public string Kind => "автозапуск";

    public bool Exists(string name) => false;

    public bool Uninstall(string name) => false;

    public void Install(AutostartRequest request) => throw Unsupported();

    public void Start(string name) => throw Unsupported();

    public void Stop(string name) => throw Unsupported();

    private static PlatformNotSupportedException Unsupported() => new(
        "Автозапуск умеет пока только Windows (задача Планировщика). Для macOS нужен агент "
        + "launchd, для Linux — пользовательский юнит systemd. В контейнере автозапуск задаёт "
        + "политика перезапуска Docker, команда install там не нужна.");
}

/// <summary>
/// Общая часть реализаций: вызов штатной утилиты ОС. Вывод у таких утилит локализован,
/// поэтому решение принимается по коду возврата, а текст идёт человеку как есть.
/// </summary>
public abstract class ProcessAutostartInstaller
{
    public abstract string Kind { get; }

    protected static ProcessResult Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Утилита отвечает на языке системы: читаем в кодировке консоли, иначе в
            // сообщении об ошибке будут кракозябры вместо причины.
            StandardOutputEncoding = Console.OutputEncoding,
            StandardErrorEncoding = Console.OutputEncoding,
        };

        // Аргументы по одному: путь с пробелами не должен зависеть от правил склейки строки.
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Не удалось запустить {fileName}.");

        // Оба потока читаются разом: длинное локализованное сообщение утилиты переполнило бы
        // буфер stderr, и утилита ждала бы места, пока мы ждём конца stdout — install завис бы.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        var text = new StringBuilder(output.GetAwaiter().GetResult().Trim());
        if (error.GetAwaiter().GetResult().Trim() is { Length: > 0 } errorText)
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(errorText);
        }

        return new ProcessResult(process.ExitCode, text.ToString());
    }

    protected static void EnsureOk(ProcessResult result, string what)
    {
        if (result.ExitCode == 0) return;

        var details = result.Text.Length > 0 ? $": {result.Text}" : ".";
        throw new InvalidOperationException($"{what} — код возврата {result.ExitCode}{details}");
    }

    protected readonly record struct ProcessResult(int ExitCode, string Text);
}
