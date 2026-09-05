using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Help;

/// <summary>/start и /help — справка по командам шлюза.</summary>
public sealed class HelpCommandHandler(ITelegramBotClient bot) : ITelegramCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/start", "/help"];

    public async Task HandleAsync(TelegramCommandContext context, CancellationToken ct) =>
        await bot.SendMessage(context.ChatId, Text, cancellationToken: ct);

    private const string Text = """
        Шлюз к Claude Code. Пишите задачу обычным сообщением.

        /menu — настройки кнопками: репозиторий, модель, effort, сессии, статистика

        /new — начать новую сессию (сбросить контекст)
        /stop — прервать текущий запуск
        /status — где работаем и что происходит
        /sessions — список сессий проекта и переключение между ними
        /project — сменить репозиторий
        /skills — какие скиллы есть и запуск их кнопкой
        /usage — расход: запуски, токены, стоимость
        /model sonnet|opus|haiku|reset — сменить модель
        /effort low|medium|high|xhigh|max|reset — сколько модели думать
        /mode plan|default|acceptEdits|auto|reset — режим работы агента
        /rules — что разрешено без вопросов; /rules del <n>, /rules clear
        /audit [n] — последние записи журнала действий

        Когда агенту нужно разрешение, придёт карточка с кнопками.
        Слэш-команды самого Claude Code (например /init или /plugin:skill) передаются агенту как есть.
        """;
}
