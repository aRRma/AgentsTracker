using AgentsTracker.Gateway.Features.Chat;
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
    public IReadOnlyCollection<string> Commands { get; } =
        ["/menu", "/settings", "/status", "/sessions", "/agent", "/model", "/effort", "/mode", "/skills", "/project", "/usage"];

    public async Task HandleAsync(ChatCommandContext context, CancellationToken ct)
    {
        var (chat, user, command, argument) = context;

        switch (command)
        {
            case "/menu" or "/settings":
                await menu.OpenAsync(chat, user, ct);
                break;

            case "/status":
                await menu.OpenAsync(chat, user, ct, "status");
                break;

            case "/sessions":
                await menu.OpenAsync(chat, user, ct, "sess");
                break;

            case "/skills":
                await menu.OpenAsync(chat, user, ct, "skills");
                break;

            case "/project":
                await menu.OpenAsync(chat, user, ct, "proj");
                break;

            case "/usage":
                await menu.OpenAsync(chat, user, ct, "usage");
                break;

            // Без аргумента открываем тот же экран с кнопками: набирать значение руками
            // после текстовой подсказки — лишний шаг с телефона.
            case "/agent":
            case "/model" or "/effort" or "/mode" when argument.Length == 0:
                await menu.OpenAsync(chat, user, ct, "agent");
                break;

            case "/model":
                await ReplyAsync(chat, Agent().Apply("model:" + argument, user, chat) ?? "", ct);
                break;

            case "/effort":
                await ReplyAsync(chat, ChangeEffort(argument, user, chat), ct);
                break;

            case "/mode":
                await ReplyAsync(chat, ChangeMode(argument, user, chat), ct);
                break;
        }
    }

    private Task ReplyAsync(ChatId chat, string text, CancellationToken ct) =>
        channel.SendAsync(chat, new OutgoingMessage(text, Rich: false), ct);

    private ISettingsScreen Agent() => menu.Screen("agent");

    /// <summary>Меняет уровень усилий модели. Применяется со следующего запуска.</summary>
    private string ChangeEffort(string argument, UserId user, ChatId chat)
    {
        if (agent.Capabilities.Effort is not { } setting)
            return $"{agent.DisplayName} не поддерживает уровень усилий.";

        if (!argument.Equals("reset", StringComparison.OrdinalIgnoreCase) && setting.Resolve(argument) is null)
            return $"Не знаю уровень «{argument}». Доступно: {string.Join(", ", setting.Selectable)}, reset.";

        Agent().Apply("effort:" + argument, user, chat);

        // Берём выбранный из чата уровень, а не действующий: после reset он null, и ответ
        // должен говорить про конфиг, а не выдавать его значение за выбор.
        return store.Effort is { } level
            ? $"🎚 {setting.Describe(level)}. Применится со следующего запуска."
            : $"🎚 Effort: {options.Value.Effort ?? "по умолчанию"} — как в конфиге.";
    }

    /// <summary>
    /// Меняет режим работы. Новый ложится в state.json и переживает перезапуск,
    /// а текущий запуск доигрывает со старым.
    /// </summary>
    private string ChangeMode(string argument, UserId user, ChatId chat)
    {
        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            Agent().Apply("mode:reset", user, chat);
            return $"🔐 Режим: {options.Value.PermissionMode} — как в конфиге. Применится со следующего запуска.";
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

        if (mode == store.EffectivePermissionMode) return $"Уже {setting.Describe(mode)}.";

        Agent().Apply("mode:" + mode, user, chat);

        // Занятость спрашиваем у воркера, а не угадываем по тосту экрана.
        var note = worker.IsBusy ? "\nТекущий запуск доигрывает со старым режимом." : "";
        return $"🔐 {setting.Describe(mode)}.\nПрименится со следующего запуска.{note}";
    }
}
