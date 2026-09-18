using System.Text.RegularExpressions;

namespace AgentsTracker.Agents.Cursor;

/// <summary>Что принимает Cursor CLI через ACP: по этим значениям хост строит меню и проверяет конфиг.</summary>
public static class CursorCapabilities
{
    /// <summary>Id из <c>agent --list-models</c> для кнопок; любое другое имя — текстом <c>/model</c>.</summary>
    private static readonly string[] ModelAliases = ["cursor-grok-4.6-xhigh-fast", "composer-2.5"];

    public static readonly AgentCapabilities Instance = new(
        Model: new AgentSetting(ModelAliases, ModelAliases, ResolveModel, DescribeModel),
        Effort: null,
        PermissionMode: new AgentSetting(
            CursorPermissionModes.All, CursorPermissionModes.Selectable,
            CursorPermissionModes.Resolve, CursorPermissionModes.Describe));

    private static readonly Dictionary<string, string> ModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cursor-grok-4.6-xhigh-fast"] = "cursor-grok-4.6-xhigh-fast",
        ["grok"] = "cursor-grok-4.6-xhigh-fast",
        ["grok-4.6-xhigh-fast"] = "cursor-grok-4.6-xhigh-fast",
        ["grok 4.6 extra high fast"] = "cursor-grok-4.6-xhigh-fast",
        ["composer-2.5"] = "composer-2.5",
        ["composer"] = "composer-2.5",
    };

    /// <summary>Модель — любое имя допустимой формы: CLI сам скажет, если такой нет.</summary>
    private static string? ResolveModel(string value)
    {
        var model = value.Trim();
        if (model.Length == 0) return null;
        var resolved = ModelIds.GetValueOrDefault(model) ?? model;
        return IsSafeModel(resolved) ? resolved : null;
    }

    /// <summary>
    /// Имя модели уходит аргументом в <c>agent</c>, а на Windows это часто <c>agent.cmd</c>: его
    /// разбирает cmd.exe, и <c>&amp;</c> или <c>|</c> из чата запустили бы команду мимо карточек.
    /// </summary>
    internal static bool IsSafeModel(string model) => SafeModel.IsMatch(model);

    private static readonly Regex SafeModel = new(@"^[A-Za-z0-9][A-Za-z0-9._:/\[\]-]{0,99}$", RegexOptions.CultureInvariant);

    private static string DescribeModel(string model) => model switch
    {
        "cursor-grok-4.6-xhigh-fast" => "Grok 4.6 Extra High Fast",
        "composer-2.5" => "Composer 2.5",
        _ => model,
    };
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
