namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>Разобранные ключи команд <c>install</c> и <c>uninstall</c>.</summary>
public sealed record CliArgs(string TaskName, bool Start)
{
    public static bool TryParse(string[] args, TextWriter output, out CliArgs options)
    {
        var name = InstallCommand.DefaultTaskName;
        var start = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
                    {
                        output.WriteLine("Ключ --name требует имя записи автозапуска.");
                        options = new CliArgs(name, start);
                        return false;
                    }

                    name = args[++i];
                    break;

                case "--start":
                    start = true;
                    break;

                default:
                    output.WriteLine($"Неизвестный ключ: {args[i]}");
                    options = new CliArgs(name, start);
                    return false;
            }
        }

        options = new CliArgs(name, start);
        return true;
    }

    /// <summary>
    /// Путь к собственному exe. Через <c>dotnet run</c> здесь окажется сам dotnet — ставить
    /// в автозапуск его бессмысленно, поэтому такой случай отсекается с подсказкой.
    /// </summary>
    public static string? OwnExecutable(TextWriter output)
    {
        var path = Environment.ProcessPath;

        if (path is null)
        {
            output.WriteLine("Не удалось определить путь к исполняемому файлу.");
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Команда работает только для опубликованного приложения, а не для «dotnet run».");
            output.WriteLine("  dotnet publish src/AgentsTracker.Gateway -c Release -o <папка установки>");
            output.WriteLine("  <папка установки>/AgentsTracker.Gateway install --start");
            return null;
        }

        return path;
    }
}
