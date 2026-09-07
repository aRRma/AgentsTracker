using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsTracker.Gateway.Infrastructure.Security;

/// <summary>
/// <c>dotnet run -- protect-secrets [путь]</c>: шифрует чувствительные ключи локального конфига
/// на месте. По умолчанию — файл в папке данных; если его там нет, берётся тот, что рядом
/// с приложением, и результат переносится в папку данных.
/// </summary>
public static class ProtectSecretsCommand
{
    public const string Name = "protect-secrets";

    /// <summary>Ключи секции Gateway, которые не должны лежать открытым текстом.</summary>
    private static readonly string[] SensitiveKeys = ["BotToken", "Proxy"];

    public static int Run(string[] args, TextWriter output)
    {
        // DPAPI есть только на Windows. Промолчать нельзя: человек решит, что токен зашифрован,
        // а он останется открытым.
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine(
                "Шифрование секретов работает только на Windows (DPAPI). На других системах "
                + "держите BotToken в переменной окружения Gateway__BotToken или в файле, "
                + "закрытом правами доступа.");
            return 1;
        }

        var source = args.Length > 1 ? args[1] : FindSource();
        if (source is null || !File.Exists(source))
        {
            output.WriteLine($"Не найден appsettings.Local.json: ни в {AppPaths.DataDirectory}, ни рядом с приложением.");
            return 1;
        }

        var root = JsonNode.Parse(File.ReadAllText(source)) as JsonObject;
        if (root?[GatewayOptions.SectionName] is not JsonObject gateway)
        {
            output.WriteLine($"В {source} нет секции «{GatewayOptions.SectionName}».");
            return 1;
        }

        var changed = 0;
        foreach (var key in SensitiveKeys)
        {
            if (gateway[key]?.GetValue<string>() is not { Length: > 0 } value) continue;
            if (SecretsProtector.IsProtected(value)) continue;

            gateway[key] = SecretsProtector.Protect(value);
            changed++;
        }

        var target = AppPaths.LocalSettings;
        var moving = !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);

        // Боевой конфиг молча не затираем: устаревший файл рядом с проектом иначе снёс бы
        // рабочий токен. Перенос — только когда в папке данных ещё ничего нет.
        if (moving && File.Exists(target))
        {
            output.WriteLine($"В {target} уже есть конфиг — он и используется. Чтобы заменить его файлом {source}, удалите или переименуйте целевой.");
            return 1;
        }

        DataDirectoryAcl.Restrict(AppPaths.DataDirectory);

        File.WriteAllText(target, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));

        if (moving)
        {
            File.Delete(source);
            output.WriteLine($"Конфиг перенесён: {source} → {target}");
        }

        output.WriteLine(changed == 0
            ? "Все секреты уже зашифрованы."
            : $"Зашифровано значений: {changed}. Файл: {target}");
        return 0;
    }

    private static string? FindSource()
    {
        if (File.Exists(AppPaths.LocalSettings)) return AppPaths.LocalSettings;

        var beside = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        return File.Exists(beside) ? beside : null;
    }
}
