namespace AgentsTracker.Agents.Claude;

/// <summary>Что принимает Claude Code: по этим значениям хост строит меню и проверяет конфиг.</summary>
public static class ClaudeCapabilities
{
    /// <summary>
    /// Кнопки — семейства, а не версии: алиас CLI сам раскрывает в последнюю версию семейства
    /// с окном 1M (проверено на CLI 2.1.283). Версия на кнопке закрепила бы модель до релиза
    /// шлюза: вышедшая новая до чата бы не дошла. Версию показывает хост — ту, что ответила,
    /// а до первого ответа — <see cref="KnownVersions"/>.
    /// </summary>
    private static readonly string[] ModelAliases = ["sonnet", "opus", "fable"];

    /// <summary>
    /// Во что алиасы раскрывались при выпуске этой версии шлюза. Только подпись до первого
    /// ответа: в CLI уходит сам алиас.
    /// </summary>
    private static readonly Dictionary<string, string> KnownVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sonnet"] = "sonnet-5",
        ["opus"] = "opus-5-5",
        ["fable"] = "fable-5-1",
    };

    private static readonly string[] Families = ["sonnet", "opus", "fable", "haiku"];

    /// <summary>
    /// Имя для <c>--model</c>. Версия без префикса («/model opus-5-5», как её пишет подпись под
    /// ответом) — в полное имя: такого алиаса CLI не знает и запуск упал бы.
    /// </summary>
    internal static string CliModel(string model) =>
        Families.Any(family => model.Length > family.Length + 1
                               && model.StartsWith(family + "-", StringComparison.OrdinalIgnoreCase)
                               && char.IsAsciiDigit(model[family.Length + 1]))
            ? "claude-" + model
            : model;

    public static readonly AgentCapabilities Instance = new(
        Model: new AgentSetting(ModelAliases, ModelAliases, ResolveModel, model => KnownVersions.GetValueOrDefault(model, model)),
        Effort: new AgentSetting(EffortLevels.All, EffortLevels.All, EffortLevels.Resolve, EffortLevels.Describe),
        PermissionMode: new AgentSetting(
            PermissionModes.All, PermissionModes.Selectable, PermissionModes.Resolve, PermissionModes.Describe),
        Context: true,
        Questions: true);

    /// <summary>
    /// Модель — любая непустая строка: CLI сам скажет, если такой нет. Алиас — в каноническом
    /// написании: «/model Opus» иначе не отметил бы выбранную кнопку.
    /// </summary>
    private static string? ResolveModel(string value) =>
        value.Trim() is { Length: > 0 } model
            ? ModelAliases.FirstOrDefault(alias => alias.Equals(model, StringComparison.OrdinalIgnoreCase)) ?? model
            : null;
}

/// <summary>
/// Режимы работы — насколько свободно агент действует на машине. То, что уходит
/// в <c>--permission-mode</c>, плюс понятные названия для /mode.
/// </summary>
internal static class PermissionModes
{
    /// <summary>Всё, что принимает CLI. Допустимые значения ключа Gateway:PermissionMode.</summary>
    public static readonly string[] All =
        ["default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions"];

    /// <summary>
    /// Что переключается из чата. dontAsk и bypassPermissions исключены намеренно: снять
    /// подтверждения полностью можно только правкой конфига на машине, иначе доступ к боту
    /// означал бы доступ к машине без единой кнопки.
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
/// Сколько модели думать над задачей — уходит в <c>--effort</c>. Порядок в <see cref="All"/>
/// от дешёвого к дорогому: по нему строится клавиатура меню.
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
