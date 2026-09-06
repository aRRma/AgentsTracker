using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot.Types.ReplyMarkups;
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
    public string Key => "agent";

    public string? Apply(string argument, long userId, long chatId)
    {
        var separator = argument.IndexOf(':');
        if (separator < 0) return null;

        var value = argument[(separator + 1)..];

        return argument[..separator] switch
        {
            "model" => ApplyModel(value, userId),
            "effort" => ApplyEffort(value, userId),
            "mode" => ApplyMode(value, userId),
            _ => null,
        };
    }

    private string ApplyModel(string value, long userId)
    {
        var reset = IsReset(value);
        var model = reset ? null : agent.Capabilities.Model.Resolve(value);
        if (!reset && model is null) return "Не знаю такую модель";

        var previous = store.Model;
        store.SetModel(model);
        audit.Changed(store, userId, "model", previous, model);
        return $"Модель: {model ?? "по умолчанию"}";
    }

    private string ApplyEffort(string value, long userId)
    {
        if (agent.Capabilities.Effort is not { } setting) return $"{agent.DisplayName} не поддерживает уровень усилий";

        var reset = IsReset(value);
        var effort = reset ? null : setting.Resolve(value);
        if (!reset && effort is null) return "Не знаю такой уровень";

        var previous = store.Effort;
        store.SetEffort(effort);
        audit.Changed(store, userId, "effort", previous, effort);
        return $"Effort: {effort ?? "по умолчанию"}";
    }

    private string ApplyMode(string value, long userId)
    {
        var previous = store.EffectivePermissionMode;

        if (IsReset(value))
        {
            store.SetPermissionMode(null);
            audit.Changed(store, userId, "mode", previous, options.Value.PermissionMode);
            return $"Режим: {options.Value.PermissionMode} (из конфига)";
        }

        var setting = agent.Capabilities.PermissionMode;
        var mode = setting.Resolve(value);
        if (mode is null || !setting.IsSelectable(mode))
            return "Этот режим из чата не переключается";

        if (mode == previous) return $"Уже {mode}";

        store.SetPermissionMode(mode);
        audit.Changed(store, userId, "mode", previous, mode);
        return worker.IsBusy
            ? $"Режим: {mode} — со следующего запуска"
            : $"Режим: {mode}";
    }

    private static bool IsReset(string value) => value.Equals("reset", StringComparison.OrdinalIgnoreCase);

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
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

            🧠 Модель: <b>{E(model ?? "по умолчанию")}</b>{effortLine}
            🔐 Режим: <b>{E(caps.PermissionMode.Describe(mode))}</b>

            <i>Кнопки модели задают алиас последней модели семейства; полное имя —
            командой <code>/model &lt;имя&gt;</code>. Effort и режим меняются со следующего
            запуска. Полностью снять подтверждения из чата нельзя — только правкой
            <code>appsettings.Local.json</code> на самой машине.</i>
            """;

        var rows = new List<InlineKeyboardButton[]>();

        rows.AddRange(Group("🧠", caps.Model, model, "model", 3));
        rows.Add([Button($"🧠 {Marker(model is null)} по умолчанию", "agent:model:reset")]);

        if (caps.Effort is { } effortSetting)
        {
            rows.AddRange(Group("🎚", effortSetting, effort, "effort", 3));
            rows.Add([Button($"🎚 {Marker(effort is null)} по умолчанию", "agent:effort:reset")]);
        }

        rows.AddRange(Group("🔐", caps.PermissionMode, mode, "mode", 2));
        rows.Add([Button($"🔐 {Marker(store.PermissionMode is null)} из конфига ({options.Value.PermissionMode})", "agent:mode:reset")]);

        rows.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(rows));
    }

    /// <summary>Ряды кнопок одной настройки; эмодзи группы на каждой кнопке — иначе в общей клавиатуре ряды не различить.</summary>
    private static IEnumerable<InlineKeyboardButton[]> Group(
        string icon, AgentSetting setting, string? current, string kind, int perRow) =>
        setting.Selectable
            .Select(value => Button($"{icon} {Marker(value == current)} {value}", $"agent:{kind}:{value}"))
            .Chunk(perRow);
}
