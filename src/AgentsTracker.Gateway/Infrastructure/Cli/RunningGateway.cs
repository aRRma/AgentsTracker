using System.ComponentModel;
using System.Diagnostics;

namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>
/// Процессы шлюза, запущенные из той же папки. Нужны обеим командам: <c>install</c> ждёт, пока
/// процесс появится, <c>uninstall</c> — пока он исчезнет. Пока exe запущен, публикация новой
/// версии поверх падает с MSB3021, поэтому снятие автозапуска обязано освобождать файл, даже
/// если шлюз подняли руками, а не задачей.
/// </summary>
public static class RunningGateway
{
    /// <summary>Все процессы этого же exe, кроме текущего.</summary>
    public static IReadOnlyList<Process> FromSamePath(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        var own = Environment.ProcessId;
        var found = new List<Process>();

        foreach (var process in Process.GetProcessesByName(name))
        {
            if (process.Id == own || !IsSamePath(process, executable))
            {
                process.Dispose();
                continue;
            }

            found.Add(process);
        }

        return found;
    }

    /// <summary>Ждёт появления процесса: команда запуска возвращает управление сразу.</summary>
    public static bool WaitForStart(string executable, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var found = FromSamePath(executable);
            foreach (var process in found) process.Dispose();
            if (found.Count > 0) return true;

            Thread.Sleep(250);
        }

        return false;
    }

    /// <summary>Останавливает оставшиеся процессы. Возвращает, скольких пришлось снять.</summary>
    public static int StopAll(string executable, TimeSpan timeout)
    {
        var stopped = 0;

        foreach (var process in FromSamePath(executable))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(timeout);
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Процесс уже завершился сам или закрыт от нас — считать это ошибкой не за что.
                }
            }
        }

        return stopped;
    }

    private static bool IsSamePath(Process process, string executable)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Чужой процесс или уже завершившийся: путь недоступен — значит, не наш.
            return false;
        }
    }
}
