using AgentsTracker.Gateway.Infrastructure.Security;

namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>
/// Служебные команды exe. Разбираются до сборки хоста: работают с файлами и автозапуском,
/// а не с каналом. Всё, что начинается с дефиса, командой не считается — это аргументы
/// конфигурации (<c>--Gateway:McpPort=…</c>).
/// </summary>
public static class ConsoleCommands
{
    /// <summary>
    /// Выполняет команду, если она указана; false — запускать надо сам шлюз. Список каналов
    /// нужен protect-secrets: какие ключи секретные, знает только модуль канала.
    /// </summary>
    public static bool TryRun(string[] args, IReadOnlyList<IChatChannelModule> channels, TextWriter output, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        var name = args[0];

        if (IsHelp(name))
        {
            PrintUsage(output);
            return true;
        }

        // Это аргументы хоста, а не команда — их разберёт конфигурация.
        if (name.StartsWith('-') || name.StartsWith('/')) return false;

        exitCode = name.ToLowerInvariant() switch
        {
            ProtectSecretsCommand.Name => ProtectSecretsCommand.Run(args, channels, output),
            InstallCommand.Name => InstallCommand.Run(args, channels, output),
            UninstallCommand.Name => UninstallCommand.Run(args, output),
            _ => Unknown(name, output),
        };

        return true;
    }

    private static bool IsHelp(string arg) =>
        arg is "help" or "--help" or "-h" or "-?" or "/?";

    private static int Unknown(string name, TextWriter output)
    {
        output.WriteLine($"Неизвестная команда: {name}");
        PrintUsage(output);
        return 1;
    }

    private static void PrintUsage(TextWriter output)
    {
        var exe = Path.GetFileName(Environment.ProcessPath) ?? "AgentsTracker.Gateway";

        output.WriteLine($"""
            {exe} — мост между Telegram и агентом командной строки.

            Запуск без аргументов поднимает шлюз.

            Команды:
              {InstallCommand.Name}                   зарегистрировать автозапуск для этого exe
              {UninstallCommand.Name}                 остановить шлюз и снять автозапуск
              {ProtectSecretsCommand.Name} [файл]     зашифровать секреты канала и Proxy, перенести конфиг в папку данных
              help                      эта справка

            Ключи install и uninstall:
              --name <имя>              имя записи автозапуска, по умолчанию «{InstallCommand.DefaultTaskName}»
              --start                   запустить сразу после регистрации

            Установка на новом месте:
              dotnet publish src/AgentsTracker.Gateway -c Release -o <папка установки>
              <папка установки>/{exe} {InstallCommand.Name} --start
            """);
    }
}
