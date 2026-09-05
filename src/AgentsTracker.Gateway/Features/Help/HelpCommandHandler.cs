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
            ? $"\n/effort {string.Join('|', setting.Selectable)}|reset — сколько модели думать"
            : "";

        return $"""
            Шлюз к {agent.DisplayName}. Пишите задачу обычным сообщением.

            /menu — настройки кнопками: репозиторий, модель, сессии, статистика

            /new — начать новую сессию (сбросить контекст)
            /stop — прервать текущий запуск
            /status — где работаем и что происходит
            /sessions — список сессий проекта и переключение между ними
            /project — сменить репозиторий
            /skills — какие скиллы есть и запуск их кнопкой
            /usage — расход: запуски, токены, стоимость
            /model {string.Join('|', caps.Model.Selectable)}|reset — сменить модель{effort}
            /mode {string.Join('|', caps.PermissionMode.Selectable)}|reset — режим работы агента
            /rules — что разрешено без вопросов; /rules del <n>, /rules clear
            /audit [n] — последние записи журнала действий

            Когда агенту нужно разрешение, придёт карточка с кнопками.
            Слэш-команды самого {agent.DisplayName} (например /init или /plugin:skill) передаются агенту как есть.
            """;
    }
}
