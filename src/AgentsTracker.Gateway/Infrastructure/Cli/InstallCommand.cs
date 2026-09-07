using AgentsTracker.Gateway.Infrastructure.Autostart;
using AgentsTracker.Gateway.Infrastructure.Configuration;
using AgentsTracker.Gateway.Infrastructure.Security;

namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>
/// <c>install</c>: регистрирует автозапуск для уже опубликованного exe и приводит в порядок
/// конфиг. Ничего никуда не копирует — где лежит файл, оттуда и будет запускаться, поэтому
/// команда одинаково работает и после публикации из исходников, и для папки, принесённой
/// с другой машины.
/// </summary>
public static class InstallCommand
{
    public const string Name = "install";
    public const string DefaultTaskName = "AgentsTracker Gateway";

    private const string Description = "Мост между Telegram и агентом командной строки";

    public static int Run(string[] args, TextWriter output)
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

        PrepareConfig(directory, output);

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
    /// Приводит конфиг к боевому виду: единственный файл — в папке данных, секреты в нём
    /// зашифрованы. Рядом с exe его быть не должно: <c>dotnet publish</c> копирует туда
    /// appsettings.Local.json из папки проекта вместе с открытым токеном.
    /// </summary>
    private static void PrepareConfig(string directory, TextWriter output)
    {
        var beside = Path.Combine(directory, "appsettings.Local.json");
        var hasData = File.Exists(AppPaths.LocalSettings);

        if (hasData || File.Exists(beside))
        {
            // Без пути — команда сама возьмёт файл из папки данных; с путём — перенесёт его туда.
            string[] arguments = hasData
                ? [ProtectSecretsCommand.Name]
                : [ProtectSecretsCommand.Name, beside];

            ProtectSecretsCommand.Run(arguments, output);
        }
        else
        {
            output.WriteLine($"Конфига нет. Положите заполненный appsettings.Local.json в {AppPaths.DataDirectory}.");
        }

        if (File.Exists(beside))
        {
            File.Delete(beside);
            output.WriteLine($"Удалён {beside}: секретам рядом с exe не место.");
        }

        DataDirectoryAcl.Restrict(AppPaths.DataDirectory);
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
