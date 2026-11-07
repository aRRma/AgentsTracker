using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Topics;

/// <summary>
/// <c>/topic</c> открывает «Тип темы», и туда же уходит любой текст новой темы вместо запуска:
/// без типа задача ушла бы в беседу, хотя человек, может быть, писал про проект.
/// </summary>
public sealed class TopicHandler(SessionStore store, SettingsMenuCoordinator menu) : IChatCommandHandler, IChatTextHandler
{
    public IReadOnlyCollection<string> Commands { get; } = ["/topic"];

    public Task HandleAsync(ChatCommandContext context, CancellationToken ct) =>
        menu.OpenAsync(context.Chat, ct, TopicScreen.ScreenKey);

    public async Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct)
    {
        if (!store.For(message.Chat).IsNewTopic) return false;

        await menu.OpenAsync(message.Chat, ct, TopicScreen.ScreenKey);
        return true;
    }
}
