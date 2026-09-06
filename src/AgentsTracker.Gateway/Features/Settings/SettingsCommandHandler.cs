using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Команды, открывающие меню, и их текстовые формы: <c>/mode plan</c>, <c>/model sonnet</c>,
/// <c>/effort high</c> применяются через тот же экран «Агент», что и кнопки, — логика одна.
/// </summary>
public sealed class SettingsCommandHandler(
    ITelegramBotClient bot,
    SettingsMenuCoordinator menu,
    SessionStore store,
    IAgentBackend agent,
    ChatWorker worker,
    IOptions<GatewayOptions> options) : ITelegramCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } =
        ["/menu", "/settings", "/status", "/sessions", "/agent", "/model", "/effort", "/mode", "/skills", "/project", "/usage"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct)
    {
        var (chatId, userId, command, argument) = context;

        switch (command)
        {
            case "/menu" or "/settings":
                await menu.OpenAsync(chatId, userId, ct);
                break;

            case "/status":
                await menu.OpenAsync(chatId, userId, ct, "status");
                break;

            case "/sessions":
                await menu.OpenAsync(chatId, userId, ct, "sess");
                break;

            case "/skills":
                await menu.OpenAsync(chatId, userId, ct, "skills");
                break;

            case "/project":
                await menu.OpenAsync(chatId, userId, ct, "proj");
                break;

            case "/usage":
                await menu.OpenAsync(chatId, userId, ct, "usage");
                break;

            // Команда без аргумента открывает тот же экран с кнопками, что и меню: набирать
            // значение руками после подсказки текстом — лишний шаг с телефона.
            case "/agent":
            case "/model" or "/effort" or "/mode" when argument.Length == 0:
                await menu.OpenAsync(chatId, userId, ct, "agent");
                break;

            case "/model":
                await bot.SendMessage(chatId, Agent().Apply("model:" + argument, userId, chatId) ?? "", cancellationToken: ct);
                break;

            case "/effort":
                await bot.SendMessage(chatId, ChangeEffort(argument, userId, chatId), cancellationToken: ct);
                break;

            case "/mode":
                await bot.SendMessage(chatId, ChangeMode(argument, userId, chatId), cancellationToken: ct);
                break;
        }
    }

    private ISettingsScreen Agent() => menu.Screen("agent");

    /// <summary>Меняет уровень усилий модели. Применяется со следующего запуска.</summary>
    private string ChangeEffort(string argument, long userId, long chatId)
    {
        if (agent.Capabilities.Effort is not { } setting)
            return $"{agent.DisplayName} не поддерживает уровень усилий.";

        if (!argument.Equals("reset", StringComparison.OrdinalIgnoreCase) && setting.Resolve(argument) is null)
            return $"Не знаю уровень «{argument}». Доступно: {string.Join(", ", setting.Selectable)}, reset.";

        Agent().Apply("effort:" + argument, userId, chatId);

        // Именно выбранный из чата уровень, а не действующий: после reset он null,
        // и ответ должен говорить про конфиг, а не повторять его значение как выбранное.
        return store.Effort is { } level
            ? $"🎚 {setting.Describe(level)}. Применится со следующего запуска."
            : $"🎚 Effort: {options.Value.Effort ?? "по умолчанию"} — как в конфиге.";
    }

    /// <summary>
    /// Меняет режим работы агента. Новый режим ложится в state.json
    /// и переживает перезапуск; текущий запуск доигрывает со старым.
    /// </summary>
    private string ChangeMode(string argument, long userId, long chatId)
    {
        if (argument.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            Agent().Apply("mode:reset", userId, chatId);
            return $"🔐 Режим: {options.Value.PermissionMode} — как в конфиге. Применится со следующего запуска.";
        }

        var setting = agent.Capabilities.PermissionMode;
        var mode = setting.Resolve(argument);

        if (mode is null || !setting.IsSelectable(mode))
        {
            // Режимы без подтверждений сюда не попадают намеренно: полное снятие
            // подтверждений остаётся правкой конфига на самой машине.
            return $"""
                Не знаю режим «{argument}». Доступно: {string.Join(", ", setting.Selectable)}, reset.
                Снять подтверждения полностью можно только в appsettings.Local.json на самой машине.
                """;
        }

        if (mode == store.EffectivePermissionMode) return $"Уже {setting.Describe(mode)}.";

        Agent().Apply("mode:" + mode, userId, chatId);

        // Занятость спрашиваем у воркера, а не угадываем по тексту тоста экрана.
        var note = worker.IsBusy ? "\nТекущий запуск доигрывает со старым режимом." : "";
        return $"🔐 {setting.Describe(mode)}.\nПрименится со следующего запуска.{note}";
    }
}
