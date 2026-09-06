using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Help;

/// <summary>/start и /help — справка по командам шлюза.</summary>
public sealed class HelpCommandHandler(ITelegramBotClient bot, IAgentBackend agent) : ITelegramCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/start", "/help"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct) =>
        await bot.SendMessage(context.ChatId, Text(), cancellationToken: ct);

    private string Text()
    {
        var caps = agent.Capabilities;
        var effort = caps.Effort is { } setting
            ? $", /effort {string.Join('|', setting.Selectable)}|reset"
            : "";

        return $"""
            Шлюз к {agent.DisplayName}. Пишите задачу обычным сообщением.

            /menu — всё кнопками
            /status — что происходит, остаток тарифа
            /sessions — сессия
            /agent — модель, effort, режим
            /skills — скиллы кнопкой, включение плагинов
            /project — сменить репозиторий
            /usage — лимиты и расход
            /rules — правила «всегда»; /rules del <n>, /rules clear
            /audit [n] — журнал действий

            Текстом: /new — новая сессия, /stop — прервать запуск,
            /model {string.Join('|', caps.Model.Selectable)}|reset{effort}, /mode {string.Join('|', caps.PermissionMode.Selectable)}|reset.

            Когда агенту нужно разрешение, придёт карточка с кнопками.
            Слэш-команды самого {agent.DisplayName} (например /init или /plugin:skill) передаются агенту как есть.
            """;
    }
}
