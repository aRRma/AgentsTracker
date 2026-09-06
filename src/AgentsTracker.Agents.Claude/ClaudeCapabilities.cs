namespace AgentsTracker.Agents.Claude;

/// <summary>Настройки, которые принимает Claude Code, — в виде, по которому хост строит меню и проверяет конфиг.</summary>
public static class ClaudeCapabilities
{
    /// <summary>Алиасы семейств для кнопок; полное имя (<c>claude-sonnet-5</c>) вводится текстом.</summary>
    private static readonly string[] ModelAliases = ["opus", "sonnet", "haiku", "fable"];

    public static readonly AgentCapabilities Instance = new(
        Model: new AgentSetting(ModelAliases, ModelAliases, ResolveModel, model => model),
        Effort: new AgentSetting(EffortLevels.All, EffortLevels.All, EffortLevels.Resolve, EffortLevels.Describe),
        PermissionMode: new AgentSetting(
            PermissionModes.All, PermissionModes.Selectable, PermissionModes.Resolve, PermissionModes.Describe));

    /// <summary>Модель — любая непустая строка: CLI сам скажет, если такой нет.</summary>
    private static string? ResolveModel(string value) =>
        value.Trim() is { Length: > 0 } model ? model : null;
}

/// <summary>
/// Режимы работы агента (насколько свободно он действует на машине) — то, что уходит
/// в <c>--permission-mode</c>, плюс человеческие названия для команды /mode.
/// </summary>
internal static class PermissionModes
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

/// <summary>
/// Сколько модели думать над задачей — то, что уходит в <c>--effort</c>.
/// Порядок в <see cref="All"/> — от дешёвого к дорогому, на нём строится клавиатура меню.
/// </summary>
internal static class EffortLevels
{
    public static readonly string[] All = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Каноническое имя уровня по тому, что написал пользователь. null — не узнали.</summary>
    public static string? Resolve(string value)
    {
        var trimmed = value.Trim();
        return All.FirstOrDefault(level => level.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static string Describe(string level) => level switch
    {
        "low" => "low — отвечает быстро, почти не рассуждает",
        "medium" => "medium — баланс скорости и качества",
        "high" => "high — думает дольше, лучше на сложных задачах",
        "xhigh" => "xhigh — думает очень долго",
        "max" => "max — максимум рассуждений, самый долгий",
        _ => level,
    };
}
