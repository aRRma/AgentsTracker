using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Question;

/// <summary>
/// Текст после «/ask» без аргумента или кнопки «Вопрос» — это вопрос, а не задача в сессию.
/// Регистрируется раньше постановки в очередь, иначе текст ушёл бы в сессию проекта.
/// </summary>
public sealed class QuestionTextHandler(IChatChannel channel, QuestionLauncher launcher) : IChatTextHandler
{
    private static readonly string[] CancelWords = ["отмена", "-", "cancel"];

    public async Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct)
    {
        var (chat, user) = (message.Chat, message.User);

        // С картинкой не берём: вопросу вложения не передаются, и она пропала бы молча.
        // Ожидание остаётся — вопрос пришлют следующим сообщением.
        if (message.Attachments.Count > 0) return false;

        if (!launcher.TryTake(user)) return false;

        var text = message.Text.Trim();

        if (text.Length == 0 || CancelWords.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            await channel.SendAsync(chat, new OutgoingMessage("Вопрос не задан.", Rich: false), ct);
            return true;
        }

        await channel.SendAsync(chat, new OutgoingMessage(launcher.Ask(chat, user, text), Rich: false), ct);
        return true;
    }
}
