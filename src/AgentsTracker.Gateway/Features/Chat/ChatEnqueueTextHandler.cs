using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Последний текстовый обработчик: всё, что никто не забрал, — промпт агенту.
/// Регистрируется последним, поэтому всегда возвращает true.
/// </summary>
public sealed class ChatEnqueueTextHandler(
    IChatChannel channel, ChatWorker worker, SessionStore store, IAuditLog audit) : IChatTextHandler
{
    public async Task<bool> TryHandleAsync(ChatId chat, UserId user, string text, CancellationToken ct)
    {
        // Превью, а не весь промпт: в него могли вставить токен или содержимое файла.
        audit.Write(AuditEvent.Now(AuditKinds.Message, $"text: {Text.Preview(text)}", user, chat, store.ProjectPath, store.SessionId));

        // Занятость проверяем до постановки в очередь: иначе сообщение увидит уже
        // начавшуюся собственную обработку.
        var wasBusy = worker.IsBusy;

        // Команды шлюза сюда не доходят, значит это скилл агента. Считаем запуски, чтобы
        // экран скиллов знал, что зовут чаще.
        if (text.StartsWith('/')) store.RecordSkillUse(CommandName(text));

        // Активный чат ставит ChatWorker перед запуском: сделай это здесь — и карточки
        // идущего запуска уедут в чат другого пользователя.
        worker.Enqueue(chat, user, text);

        if (wasBusy)
            await channel.SendAsync(chat, new OutgoingMessage("📥 Добавлено в очередь — отвечу, как освобожусь.", Rich: false), ct);

        return true;
    }

    /// <summary>Первое слово без «@имябота» — канал подставляет его при выборе из подсказок.</summary>
    private static string CommandName(string text)
    {
        var word = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var at = word.IndexOf('@');
        return at < 0 ? word : word[..at];
    }
}
