using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Features.Settings.Screens;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Команды меню и их текстовые формы. <c>/mode plan</c>, <c>/model sonnet</c>,
/// <c>/effort high</c> идут через тот же экран «Агент», что и кнопки — логика одна.
/// </summary>
public sealed class SettingsCommandHandler(
    IChatChannel channel,
    SettingsMenuCoordinator menu,
    SessionStore store,
    IAgentBackend agent,
    ChatWorker worker,
    IOptions<GatewayOptions> options) : IChatCommandHandler
{
    // Команды, которые просто открывают экран меню. Список Commands собран из этого же словаря:
    // новая команда, правленная в двух местах, либо не дошла бы сюда, либо ничего не открыла бы.
    private static readonly Dictionary<string, string> ScreenCommands = new()
    {
        ["/status"] = "status",
        ["/sessions"] = "sess",
        ["/skills"] = "skills",
        ["/project"] = "proj",
        ["/usage"] = "usage",
    };

    public IReadOnlyCollection<string> Commands { get; } =
        ["/menu", "/settings", .. ScreenCommands.Keys, "/agent", "/model", "/effort", "/mode"];

    public async Task HandleAsync(ChatCommandContext context, CancellationToken ct)
    {
        var (chat, user, command, argument, _) = context;

        if (ScreenCommands.TryGetValue(command, out var screen))
        {
            await menu.OpenAsync(chat, ct, screen);
            return;
        }

        switch (command)
        {
            case "/menu" or "/settings":
                await menu.OpenAsync(chat, ct);
                break;

            // Без аргумента открываем тот же экран с кнопками: набирать значение руками
            // после текстовой подсказки — лишний шаг с телефона.
            case "/agent":
            case "/model" or "/effort" or "/mode" when argument.Length == 0:
                await menu.OpenAsync(chat, ct, "agent");
                break;

            case "/model":
                var warning = CacheNote(Agent().CacheWarning("model:" + argument, chat));
                await channel.SayAsync(chat, (Agent().Apply(AgentScreen.Now + "model:" + argument, user, chat) ?? "") + warning, ct);
                break;

            case "/effort":
                await channel.SayAsync(chat, ChangeEffort(argument, user, chat), ct);
                break;

            case "/mode":
                await channel.SayAsync(chat, ChangeMode(argument, user, chat), ct);
                break;
        }
    }

    private AgentScreen Agent() => (AgentScreen)menu.Screen("agent");

    /// <summary>Предупреждение о кэше к ответу команды — её уже выполнили, так что это не вопрос, а факт.</summary>
    private static string CacheNote(string? warning) => warning is null ? "" : $"\n🧊 {warning}";

    /// <summary>Меняет effort. Применяется со следующего запуска.</summary>
    private string ChangeEffort(string argument, UserId user, ChatId chat)
    {
        if (agent.Capabilities.Effort is not { } setting)
            return $"{agent.DisplayName} не поддерживает effort.";

        if (!argument.Equals("reset", StringComparison.OrdinalIgnoreCase) && setting.Resolve(argument) is null)
            return $"Не знаю effort «{argument}». Доступно: {string.Join(", ", setting.Selectable)}, reset.";

        Agent().Apply("effort:" + argument, user, chat);

        // Берём выбранный из чата уровень, а не действующий: после reset он null, и ответ
        // должен говорить про конфиг, а не выдавать его значение за выбор.
        return store.For(chat).Effort is { } level
            ? $"🎚 {setting.Describe(level)}. Применится со следующего запуска."
            : $"🎚 Effort: {store.For(chat).EffectiveEffort ?? "по умолчанию"} — как в конфиге.";
    }

    /// <summary>
    /// Меняет режим работы. Новый ложится в state.json и переживает перезапуск,
    /// а текущий запуск доигрывает со старым.
    /// </summary>
    private string ChangeMode(string argument, UserId user, ChatId chat)
    {
        if (AgentScreen.ModeRefusal(store.For(chat)) is { } refusal) return refusal + ".";

        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            var warning = CacheNote(Agent().CacheWarning("mode:reset", chat));
            Agent().Apply(AgentScreen.Now + "mode:reset", user, chat);
            return $"🔐 Режим: {options.Value.PermissionMode} — как в конфиге. Применится со следующего запуска.{warning}";
        }

        var setting = agent.Capabilities.PermissionMode;
        var mode = setting.Resolve(argument);

        if (mode is null || !setting.IsSelectable(mode))
        {
            // Режимов без подтверждений здесь нет намеренно: снять их можно только
            // правкой конфига на самой машине.
            return $"""
                Не знаю режим «{argument}». Доступно: {string.Join(", ", setting.Selectable)}, reset.
                Снять подтверждения полностью можно только в appsettings.Local.json на самой машине.
                """;
        }

        if (mode == store.For(chat).EffectivePermissionMode) return $"Уже {setting.Describe(mode)}.";

        var cache = CacheNote(Agent().CacheWarning("mode:" + mode, chat));
        Agent().Apply(AgentScreen.Now + "mode:" + mode, user, chat);

        // Занятость спрашиваем у воркера, а не угадываем по тосту экрана.
        var note = worker.IsBusy(chat) ? "\nТекущий запуск доигрывает со старым режимом." : "";
        return $"🔐 {setting.Describe(mode)}.\nПрименится со следующего запуска.{note}{cache}";
    }
}
