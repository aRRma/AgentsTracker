using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Help;

/// <summary>/start и /help — справка по командам шлюза.</summary>
public sealed class HelpCommandHandler(IChatChannel channel, IAgentBackend agent) : IChatCommandHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/start", "/help"];

    public async Task HandleAsync(ChatCommandContext context, CancellationToken ct) =>
        await channel.SendAsync(context.Chat, new OutgoingMessage(Text(), Rich: false), ct);

    private string Text()
    {
        var caps = agent.Capabilities;
        var effort = caps.Effort is { } setting
            ? $", /effort {string.Join('|', setting.Selectable)}|reset"
            : "";

        return $"""
            Шлюз к {agent.DisplayName}. Пишите задачу обычным сообщением.

            /menu — всё кнопками
            /status — что происходит, лимиты тарифа
            /sessions — сессия
            /ask <вопрос> — ответ вне сессии и проекта, текущая сессия не затронется
            /agent — модель, effort, режим
            /skills — скиллы кнопкой, включение плагинов
            /project — сменить проект
            /topic — тип темы: проект или беседа
            /usage — расход и лимиты тарифа
            /rules — правила «всегда»; /rules del <n>, /rules clear
            /audit [n] — журнал действий

            Текстом: /new — новая сессия, /stop — прервать запуск (в теме — только её, в ленте — всё),
            /model {string.Join('|', caps.Model.Selectable)}|reset{effort}, /mode {string.Join('|', caps.PermissionMode.Selectable)}|reset.

            Картинку (PNG, JPEG) можно прислать прямо в чат — подпись станет задачей.
            Если на скриншоте мелкий текст, отправляйте «как файл»: Telegram жмёт фото.

            В меню «📦 Контекст» — насколько заполнен контекст сессии, из чего он состоит, сжать его или начать новую сессию.
            Под каждым ответом — сессия и модель, которая ответила, с effort в скобках: opus-5-5(H); L, M, H, XH, MAX — от low до max.

            Темы: создайте тему в чате с ботом — шлюз спросит её тип (/topic). У каждой темы свой проект или беседа, своя сессия, модель и effort, и темы работают параллельно. Беседа — тема без проекта: только поиск в сети, без файлов и картинок, режим в ней не меняется.

            Когда агенту нужно подтверждение, придёт карточка с кнопками.
            Слэш-команды самого {agent.DisplayName} (например /init или /plugin:skill) передаются агенту как есть.
            """;
    }
}
