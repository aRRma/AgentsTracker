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
            /agent — модель, effort, режим
            /skills — скиллы кнопкой, включение плагинов
            /project — сменить проект
            /usage — расход и лимиты тарифа
            /rules — правила «всегда»; /rules del <n>, /rules clear
            /audit [n] — журнал действий

            Текстом: /new — новая сессия, /stop — прервать запуск,
            /model {string.Join('|', caps.Model.Selectable)}|reset{effort}, /mode {string.Join('|', caps.PermissionMode.Selectable)}|reset.

            Картинку (PNG, JPEG) можно прислать прямо в чат — подпись станет задачей.
            Если на скриншоте мелкий текст, отправляйте «как файл»: Telegram жмёт фото.

            Когда агенту нужно подтверждение, придёт карточка с кнопками.
            Слэш-команды самого {agent.DisplayName} (например /init или /plugin:skill) передаются агенту как есть.
            """;
    }
}
