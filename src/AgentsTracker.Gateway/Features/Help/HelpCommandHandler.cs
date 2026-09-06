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

            /menu — настройки кнопками: статус, сессии, агент, скиллы, репозиторий

            /status — где работаем, что происходит, остаток тарифа шкалами
            /sessions — сессии проекта: активная, переключение, новая, остановка запуска
            /new — начать новую сессию (сбросить контекст)
            /stop — прервать текущий запуск

            /model {string.Join('|', caps.Model.Selectable)}|reset — сменить модель; без аргумента — экран «Агент»{effort}
            /mode {string.Join('|', caps.PermissionMode.Selectable)}|reset — режим работы агента

            /skills — какие скиллы есть и запуск их кнопкой
            /project — сменить репозиторий
            /usage — остаток тарифа и расход: запуски, токены
            /rules — что разрешено без вопросов; /rules del <n>, /rules clear
            /audit [n] — последние записи журнала действий

            Когда агенту нужно разрешение, придёт карточка с кнопками.
            Слэш-команды самого {agent.DisplayName} (например /init или /plugin:skill) передаются агенту как есть.
            """;
    }
}
