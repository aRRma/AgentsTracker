using AgentsTracker.Gateway.Infrastructure.Autostart;
using AgentsTracker.Gateway.Infrastructure.Security;

namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>
/// <c>install</c>: регистрирует автозапуск опубликованного exe и приводит в порядок конфиг.
/// Ничего не копирует — запускаться будет оттуда, где лежит, поэтому команда одинаково
/// работает и после публикации из исходников, и для папки с другой машины.
/// </summary>
public static class InstallCommand
{
    public const string Name = "install";
    public const string DefaultTaskName = "AgentsTracker Gateway";

    private const string Description = "Мост между Telegram и агентом командной строки";

    public static int Run(string[] args, IReadOnlyList<IChatChannelModule> channels, TextWriter output)
    {
        if (!CliArgs.TryParse(args, output, out var options)) return 1;

        var exe = CliArgs.OwnExecutable(output);
        if (exe is null) return 1;

        if (Container.Detected)
        {
            output.WriteLine(
                "Внутри контейнера автозапуск задаёт политика перезапуска Docker "
                + "(restart: unless-stopped), команда install здесь не нужна.");
            return 1;
        }

        var directory = Path.GetDirectoryName(exe)!;
        if (directory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"Внимание: {exe} — это папка сборки. Для постоянной работы опубликуйте приложение:");
            output.WriteLine("  dotnet publish src/AgentsTracker.Gateway -c Release -o <папка установки>");
        }

        // С конфигом, который шлюз читать откажется (ключи канала в старом месте), задача
        // Планировщика трижды перезапустит падающий exe и сдастся, а install отчитается
        // об успехе. Лучше не регистрировать.
        if (!PrepareConfig(directory, channels, output))
        {
            output.WriteLine("Автозапуск не зарегистрирован: сначала приведите конфиг в порядок и повторите install.");
            return 1;
        }

        var installer = AutostartInstaller.ForCurrentOs();
        try
        {
            if (installer.Exists(options.TaskName))
            {
                output.WriteLine($"Уже зарегистрировано, перезаписываю: {options.TaskName}");
                installer.Stop(options.TaskName);
            }

            installer.Install(new AutostartRequest(options.TaskName, Description, exe, directory));

            if (options.Start) installer.Start(options.TaskName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            output.WriteLine(ex.Message);
            return 1;
        }

        Report(installer, options, exe, output);
        return 0;
    }

    /// <summary>
    /// Приводит конфиг к боевому виду: один файл в папке данных, секреты зашифрованы.
    /// Рядом с exe его быть не должно — <c>dotnet publish</c> копирует туда
    /// appsettings.Local.json проекта вместе с открытым токеном. false — protect-secrets
    /// отказался, и с таким конфигом шлюз не стартует.
    /// </summary>
    private static bool PrepareConfig(string directory, IReadOnlyList<IChatChannelModule> channels, TextWriter output)
    {
        var beside = Path.Combine(directory, "appsettings.Local.json");
        var ok = true;

        if (File.Exists(AppPaths.LocalSettings))
        {
            // Файл рядом с exe всё равно не применится — боевой лежит в папке данных, —
            // но молча удалять его нельзя: там могли принести новые значения.
            if (File.Exists(beside))
            {
                File.Delete(beside);
                output.WriteLine(
                    $"Боевой конфиг — {AppPaths.LocalSettings}. Файл рядом с exe удалён, его значения "
                    + "не применены: чтобы поставить их, удалите конфиг в папке данных и повторите.");
            }

            ok = ProtectSecretsCommand.Run([ProtectSecretsCommand.Name], channels, output) == 0;
            if (!ok) output.WriteLine($"Проверьте {AppPaths.LocalSettings}: секреты остались как есть.");
        }
        else if (File.Exists(beside))
        {
            // protect-secrets сам перенесёт файл и зашифрует секреты. Копию рядом с exe
            // не удаляем сами: на его ошибке остались бы вообще без конфига.
            ok = ProtectSecretsCommand.Run([ProtectSecretsCommand.Name, beside], channels, output) == 0;
        }
        else
        {
            output.WriteLine($"Конфига нет. Положите заполненный appsettings.Local.json в {AppPaths.DataDirectory}.");
        }

        DataDirectoryAcl.Restrict(AppPaths.DataDirectory);
        return ok;
    }

    private static void Report(IAutostartInstaller installer, CliArgs options, string exe, TextWriter output)
    {
        var settings = LocalConfig.Read();

        output.WriteLine();
        output.WriteLine($"Готово. {Capitalize(installer.Kind)}: {options.TaskName}");
        output.WriteLine($"  запуск:      {exe}");
        output.WriteLine($"  данные:      {AppPaths.DataDirectory}");
        output.WriteLine($"  конфиг:      {settings.Summary}");
        output.WriteLine($"  порты:       MCP {settings.McpPort}, монитор {settings.MonitorDescription}");

        if (options.Start)
        {
            var running = RunningGateway.WaitForStart(exe, TimeSpan.FromSeconds(10));
            output.WriteLine(running
                ? "  состояние:   запущен, в Telegram придёт «Шлюз запущен»"
                : "  состояние:   команда запуска отправлена, процесс пока не виден — смотрите Планировщик заданий");
        }
        else
        {
            output.WriteLine($"  состояние:   зарегистрирован, поднимется при входе в Windows");
        }
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
