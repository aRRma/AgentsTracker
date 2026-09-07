using AgentsTracker.Gateway.Infrastructure.Autostart;

namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>
/// <c>uninstall</c>: останавливает шлюз и снимает автозапуск. Данные не трогает — их удаление
/// должно быть отдельным осознанным действием, там сессии, правила «Всегда» и аудит.
/// Этой же командой освобождают файлы перед публикацией новой версии поверх старой.
/// </summary>
public static class UninstallCommand
{
    public const string Name = "uninstall";

    public static int Run(string[] args, TextWriter output)
    {
        if (!CliArgs.TryParse(args, output, out var options)) return 1;

        var installer = AutostartInstaller.ForCurrentOs();

        try
        {
            if (installer.Uninstall(options.TaskName))
                output.WriteLine($"Снято: {installer.Kind} «{options.TaskName}». Шлюз остановлен.");
            else
                output.WriteLine($"Записи «{options.TaskName}» нет — снимать нечего.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            output.WriteLine(ex.Message);
            return 1;
        }

        // Шлюз могли поднять и руками, мимо автозапуска. Пока процесс жив, публикация новой
        // версии поверх не пройдёт, а именно за этим команду и зовут перед обновлением.
        if (Environment.ProcessPath is { } exe)
        {
            var stopped = RunningGateway.StopAll(exe, TimeSpan.FromSeconds(10));
            if (stopped > 0) output.WriteLine($"Остановлено запущенных вручную: {stopped}");
        }

        output.WriteLine($"Данные остались: {AppPaths.DataDirectory}");
        return 0;
    }
}
