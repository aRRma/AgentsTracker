using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Агент одним экраном: модель, effort и режим работы. Три настройки меняют вместе —
/// «сложная задача: opus, high, acceptEdits» — и по отдельным экранам это три захода
/// в меню. Аргумент нажатия — «model:sonnet», «effort:high», «mode:plan», значение
/// «reset» возвращает настройку к конфигу. Списки значений — из <see cref="AgentCapabilities"/>;
/// из чата переключаются только <see cref="AgentSetting.Selectable"/>.
/// </summary>
public sealed class AgentScreen(
    SessionStore store, IAgentBackend agent, ChatWorker worker, IAuditLog audit, IOptions<GatewayOptions> options) : ISettingsScreen
{
    /// <summary>
    /// Приставка команды, набранной руками: это уже решение, переспрашивать его кнопками нечем —
    /// команда выполняется сразу, а предупреждение о кэше добавляет к ответу обработчик команды.
    /// </summary>
    internal const string Now = "now:";

    private const string Yes = "yes:";
    private const string Stale = "Подтверждение устарело — нажмите кнопку ещё раз";

    private readonly PendingConfirmations _confirmations = new();

    public string Key => "agent";

    public void Open(UserId user) => _confirmations.Cancel(user);

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        var now = argument.StartsWith(Now, StringComparison.Ordinal);
        if (now) argument = argument[Now.Length..];

        if (argument == "no")
        {
            _confirmations.Cancel(user);
            return null;
        }

        if (argument.StartsWith(Yes, StringComparison.Ordinal))
        {
            argument = argument[Yes.Length..];
            // Сессию сверяем с той, о которой спрашивали: после смены сессии «Да» сбросил бы
            // кэш другой ветки, про которую вопроса не было.
            if (!_confirmations.TryConfirm(user, argument, store.SessionId)) return Stale;
        }
        else if (!now && CacheWarning(argument) is not null)
        {
            _confirmations.Ask(user, argument, store.SessionId);
            return null;
        }

        var separator = argument.IndexOf(':');
        if (separator < 0) return null;

        var value = argument[(separator + 1)..];

        return argument[..separator] switch
        {
            "model" => ApplyModel(value, user),
            "effort" => ApplyEffort(value, user),
            "mode" => ApplyMode(value, user),
            _ => null,
        };
    }

    private string ApplyModel(string value, UserId user)
    {
        var reset = IsReset(value);
        var model = reset ? null : agent.Capabilities.Model.Resolve(value);
        if (!reset && model is null) return "Не знаю такую модель";

        var setting = agent.Capabilities.Model;
        var previous = store.ModelLabel(store.Model, setting);
        var label = store.ModelLabel(model, setting);
        store.SetModel(model);
        audit.Changed(store, user, "model", previous, label);
        return $"Модель: {label ?? "по умолчанию"}";
    }

    private string ApplyEffort(string value, UserId user)
    {
        if (agent.Capabilities.Effort is not { } setting) return $"{agent.DisplayName} не поддерживает уровень усилий";

        var reset = IsReset(value);
        var effort = reset ? null : setting.Resolve(value);
        if (!reset && effort is null) return "Не знаю такой уровень";

        var previous = store.Effort;
        store.SetEffort(effort);
        audit.Changed(store, user, "effort", previous, effort);
        return $"Effort: {effort ?? "по умолчанию"}";
    }

    private string ApplyMode(string value, UserId user)
    {
        var previous = store.EffectivePermissionMode;

        if (IsReset(value))
        {
            store.SetPermissionMode(null);
            audit.Changed(store, user, "mode", previous, options.Value.PermissionMode);
            return $"Режим: {options.Value.PermissionMode} (из конфига)";
        }

        var setting = agent.Capabilities.PermissionMode;
        var mode = setting.Resolve(value);
        if (mode is null || !setting.IsSelectable(mode))
            return "Этот режим из чата не переключается";

        if (mode == previous) return $"Уже {mode}";

        store.SetPermissionMode(mode);
        audit.Changed(store, user, "mode", previous, mode);
        return worker.IsBusy
            ? $"Режим: {mode} — со следующего запуска"
            : $"Режим: {mode}";
    }

    private static bool IsReset(string value) => value.Equals("reset", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Чем смена обернётся для кэша активной сессии; null — кэш не пострадает. Считается до
    /// смены: после неё «сейчас» уже другое, и сравнивать не с чем.
    /// </summary>
    /// <param name="argument">Нажатие или команда: «model:opus», «mode:plan», «mode:reset».</param>
    internal string? CacheWarning(string argument)
    {
        var separator = argument.IndexOf(':');
        if (separator < 0) return null;

        var kind = argument[..separator];
        var value = argument[(separator + 1)..];
        var caps = agent.Capabilities;

        var fromModel = store.ModelLabel(store.EffectiveModel, caps.Model);
        var fromMode = CacheGuard.ModeScope(caps, store.EffectivePermissionMode);
        string? toModel = fromModel;
        var toMode = fromMode;

        switch (kind)
        {
            case "model":
                toModel = store.ModelLabel(IsReset(value) ? null : caps.Model.Resolve(value), caps.Model);
                break;

            case "mode":
                var mode = IsReset(value) ? options.Value.PermissionMode : caps.PermissionMode.Resolve(value);
                if (mode is null) return null;
                toMode = CacheGuard.ModeScope(caps, mode);
                break;

            default:
                return null;
        }

        var session = store.Session(store.SessionId);
        if (!CacheGuard.WillReset(session, fromModel, toModel, fromMode, toMode)) return null;

        return $"Кэш сессии жив до {session!.CacheUntilUtc!.Value.Clock}. После смены следующий запуск "
               + $"запишет заново {session.ContextTokens.Tokens} токенов контекста и потратит заметно больше "
               + "лимита тарифа, чем обычный шаг. Дешевле сменить в начале новой сессии.";
    }

    public Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct) =>
        Task.FromResult(_confirmations.Asked(user) is { } asked && CacheWarning(asked) is { } warning
            ? Confirmation(asked, warning)
            : Render());

    private static (string Html, Keyboard Keyboard) Confirmation(string asked, string warning)
    {
        var change = asked.StartsWith("model:", StringComparison.Ordinal) ? "модель" : "режим";

        var html = $"""
            🧊 <b>Сменить {change}? Кэш сбросится</b>

            {E(warning)}
            """;

        return (html, new Keyboard([[Button("✅ Да, сменить", $"agent:{Yes}{asked}"), Button("↩️ Нет", "agent:no")]]));
    }

    private (string Html, Keyboard Keyboard) Render()
    {
        var caps = agent.Capabilities;
        var model = store.EffectiveModel;
        var effort = store.EffectiveEffort;
        var mode = store.EffectivePermissionMode;

        var effortLine = caps.Effort is { } setting
            ? $"\n🎚 Effort: <b>{E(effort is null ? "по умолчанию" : setting.Describe(effort))}</b>"
            : "";

        var html = $"""
            🤖 <b>Агент</b> — {E(agent.DisplayName)}

            🧠 Модель: <b>{E(store.ModelLabel(model, caps.Model) ?? "по умолчанию")}</b>{effortLine}
            🔐 Режим: <b>{E(caps.PermissionMode.Describe(mode))}</b>

            <i>На кнопках — последние версии моделей: выйдет новая — кнопка сама перейдёт
            на неё. Другую модель — командой
            <code>/model &lt;имя&gt;</code>. Effort и режим меняются со следующего
            запуска. Полностью снять подтверждения из чата нельзя — только правкой
            <code>appsettings.Local.json</code> на самой машине.</i>
            """;

        var rows = new List<KeyboardButton[]>();

        // На кнопке версия, в нажатии — алиас: он и уходит агенту.
        rows.AddRange(caps.Model.Selectable
            .Select(value => Button($"🧠 {Marker(value == model)} {store.ModelLabel(value, caps.Model)}", $"agent:model:{value}"))
            .Chunk(3));
        rows.Add([Button($"🧠 {Marker(model is null)} по умолчанию", "agent:model:reset")]);

        if (caps.Effort is { } effortSetting)
        {
            rows.AddRange(Group("🎚", effortSetting, effort, "effort", 3));
            rows.Add([Button($"🎚 {Marker(effort is null)} по умолчанию", "agent:effort:reset")]);
        }

        rows.AddRange(Group("🔐", caps.PermissionMode, mode, "mode", 2));
        rows.Add([Button($"🔐 {Marker(store.PermissionMode is null)} из конфига ({options.Value.PermissionMode})", "agent:mode:reset")]);

        rows.Add([BackButton]);

        return (html, new Keyboard(rows));
    }

    /// <summary>Ряды кнопок одной настройки; эмодзи группы на каждой кнопке — иначе в общей клавиатуре ряды не различить.</summary>
    private static IEnumerable<KeyboardButton[]> Group(
        string icon, AgentSetting setting, string? current, string kind, int perRow) =>
        setting.Selectable
            .Select(value => Button($"{icon} {Marker(value == current)} {value}", $"agent:{kind}:{value}"))
            .Chunk(perRow);
}
