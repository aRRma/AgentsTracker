using AgentsTracker.Gateway.Infrastructure.Security;

namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>
/// Служебные команды exe: разбираются до сборки хоста, потому что работают с файлами и
/// автозапуском, а не с Telegram. Всё, что начинается с дефиса, командой не считается —
/// это аргументы конфигурации хоста (<c>--Gateway:McpPort=…</c>).
/// </summary>
public static class ConsoleCommands
{
    /// <summary>
    /// Выполняет команду, если она указана. Возвращает false, когда запускать надо сам шлюз.
    /// </summary>
    public static bool TryRun(string[] args, TextWriter output, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        var name = args[0];

        if (IsHelp(name))
        {
            PrintUsage(output);
            return true;
        }

        // Аргументы хоста, а не команда: пусть их разбирает конфигурация.
        if (name.StartsWith('-') || name.StartsWith('/')) return false;

        exitCode = name.ToLowerInvariant() switch
        {
            ProtectSecretsCommand.Name => ProtectSecretsCommand.Run(args, output),
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
              {ProtectSecretsCommand.Name} [файл]   зашифровать BotToken и Proxy, перенести конфиг в папку данных
              help                          эта справка
            """);
    }
}
