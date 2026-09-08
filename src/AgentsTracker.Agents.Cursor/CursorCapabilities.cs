namespace AgentsTracker.Agents.Cursor;

/// <summary>Что принимает Cursor CLI через ACP: по этим значениям хост строит меню и проверяет конфиг.</summary>
public static class CursorCapabilities
{
    /// <summary>Короткие id для кнопок; полное имя модели вводится текстом.</summary>
    private static readonly string[] ModelAliases = ["auto", "composer-2.5"];

    public static readonly AgentCapabilities Instance = new(
        Model: new AgentSetting(ModelAliases, ModelAliases, ResolveModel, model => model),
        Effort: null,
        PermissionMode: new AgentSetting(
            CursorPermissionModes.All, CursorPermissionModes.Selectable,
            CursorPermissionModes.Resolve, CursorPermissionModes.Describe));

    /// <summary>Модель — любая непустая строка: CLI сам скажет, если такой нет.</summary>
    private static string? ResolveModel(string value) =>
        value.Trim() is { Length: > 0 } model ? model : null;
}

/// <summary>
/// Режимы Cursor: plan/ask — как у CLI, default — карточки на инструменты,
/// auto — разрешать без карточки. acceptEdits у Cursor нет.
/// </summary>
internal static class CursorPermissionModes
{
    public static readonly string[] All = ["plan", "ask", "default", "auto", "dontAsk"];

    /// <summary>
    /// Из чата. dontAsk исключён: снять подтверждения полностью можно только правкой
    /// конфига на машине, иначе доступ к боту означал бы доступ к машине без кнопок.
    /// </summary>
    public static readonly string[] Selectable = ["plan", "ask", "default", "auto"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plan"] = "plan",
        ["read"] = "plan",
        ["чтение"] = "plan",
        ["ask"] = "ask",
        ["вопрос"] = "ask",
        ["default"] = "default",
        ["спрашивать"] = "default",
        ["auto"] = "auto",
        ["dontask"] = "dontAsk",
    };

    public static string? Resolve(string value) => Aliases.GetValueOrDefault(value.Trim());

    public static string Describe(string mode) => mode switch
    {
        "plan" => "plan — только читает и планирует, файлы не меняет",
        "ask" => "ask — отвечает на вопросы, файлы не меняет",
        "default" => "default — спрашивает всё, что не разрешено правилами",
        "auto" => "auto — разрешает инструменты без карточки",
        "dontAsk" => "dontAsk — без подтверждений",
        _ => mode,
    };

    /// <summary>Режим сессии ACP. default/auto/dontAsk — полный агент, разница только в карточках.</summary>
    public static string AcpMode(string permissionMode) => permissionMode switch
    {
        "plan" => "plan",
        "ask" => "ask",
        _ => "agent",
    };

    /// <summary>Карточки не показываем: иначе auto из чата всё равно спрашивал бы каждый инструмент.</summary>
    public static bool AutoAllow(string permissionMode) =>
        permissionMode is "auto" or "dontAsk";
}
