namespace AgentsTracker.Gateway.Domain;

/// <summary>
/// Уровни доступа агента к машине — то, что уходит в <c>--permission-mode</c>,
/// плюс человеческие названия для команды /mode.
/// </summary>
public static class PermissionModes
{
    /// <summary>Всё, что принимает CLI. Допустимые значения ключа Gateway:PermissionMode.</summary>
    public static readonly string[] All =
        ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"];

    /// <summary>
    /// Что можно переключать из чата. dontAsk и bypassPermissions сюда не входят намеренно:
    /// снять подтверждения полностью можно только правкой конфига на самой машине,
    /// иначе доступ к боту означал бы доступ к машине без единой кнопки.
    /// </summary>
    public static readonly string[] Selectable = ["plan", "default", "acceptEdits", "auto"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plan"] = "plan",
        ["read"] = "plan",
        ["чтение"] = "plan",
        ["default"] = "default",
        ["ask"] = "default",
        ["спрашивать"] = "default",
        ["acceptedits"] = "acceptEdits",
        ["edits"] = "acceptEdits",
        ["edit"] = "acceptEdits",
        ["правки"] = "acceptEdits",
        ["auto"] = "auto",
    };

    /// <summary>Каноническое имя режима по тому, что написал пользователь. null — не узнали.</summary>
    public static string? Resolve(string value) => Aliases.GetValueOrDefault(value.Trim());

    public static string Describe(string mode) => mode switch
    {
        "plan" => "plan — только читает и планирует, файлы не меняет",
        "default" => "default — спрашивает всё, что не разрешено правилами",
        "acceptEdits" => "acceptEdits — правит файлы молча, остальное кнопками",
        "auto" => "auto — решает классификатор, кнопки почти не появляются",
        "dontAsk" => "dontAsk — без подтверждений",
        "bypassPermissions" => "bypassPermissions — без подтверждений",
        _ => mode,
    };
}
