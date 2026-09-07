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

    /// <summary>Ключи самой секции Gateway, которые не должны лежать открытым текстом.</summary>
    private static readonly string[] GatewaySecrets = ["Proxy"];

    /// <summary>Ключи канала, переехавшие в его секцию: в корне Gateway их больше не ищут и не шифруют.</summary>
    private static readonly string[] MovedChannelKeys = ["BotToken", "AllowedUserIds"];

    /// <summary>
    /// Шифрует секреты хоста и секреты выбранного канала: какие ключи в его настройках
    /// секретные, знает только модуль канала (<see cref="IChatChannelModule.SecretKeys"/>).
    /// Берём ключи всех известных каналов — шифруется всё равно только то, что есть в файле.
    /// </summary>
    public static int Run(string[] args, IReadOnlyList<IChatChannelModule> channels, TextWriter output)
    {
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

        // Токен в корне секции Gateway новый шлюз не читает, а значит и не шифрует: молча
        // перенести такой файл в папку данных значило бы положить туда токен открытым текстом
        // и отчитаться «все секреты зашифрованы». Сначала миграция, потом шифрование.
        var legacy = MovedChannelKeys.Where(key => gateway[key] is not null).ToArray();
        if (legacy.Length > 0)
        {
            output.WriteLine(
                $"В {source} ключи {string.Join(", ", legacy.Select(k => $"{GatewayOptions.SectionName}:{k}"))} "
                + $"лежат по-старому — шлюз их не читает, а protect-secrets не шифрует. "
                + $"Перенесите их в {ChannelConfiguration.SettingsSection} "
                + $"(pwsh -File scripts\\migrate-channel-settings.ps1) и повторите.");
            return 1;
        }

        var changed = Protect(gateway, GatewaySecrets);

        if (gateway["Channel"] is JsonObject { } channel && channel["Settings"] is JsonObject settings)
            changed += Protect(settings, [.. channels.SelectMany(c => c.SecretKeys).Distinct(StringComparer.Ordinal)]);

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

    /// <summary>Зашифровывает перечисленные ключи объекта на месте. Возвращает, сколько значений тронуто.</summary>
    private static int Protect(JsonObject section, IReadOnlyList<string> keys)
    {
        var changed = 0;

        foreach (var key in keys)
        {
            if (section[key]?.GetValue<string>() is not { Length: > 0 } value) continue;
            if (SecretsProtector.IsProtected(value)) continue;

            section[key] = SecretsProtector.Protect(value);
            changed++;
        }

        return changed;
    }

    private static string? FindSource()
    {
        if (File.Exists(AppPaths.LocalSettings)) return AppPaths.LocalSettings;

        var beside = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        return File.Exists(beside) ? beside : null;
    }
}
