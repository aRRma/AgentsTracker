using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Последний текстовый обработчик: всё, что никто не забрал, — промпт агенту.
/// Регистрируется последним, поэтому всегда возвращает true.
/// </summary>
public sealed class ChatEnqueueTextHandler(
    ITelegramBotClient bot, ChatWorker worker, SessionStore store, IAuditLog audit) : ITelegramTextHandler
{
    public async Task<bool> TryHandleAsync(long chatId, long userId, string text, CancellationToken ct)
    {
        audit.Write(AuditEvent.Now(AuditKinds.Message, $"text: {text}", userId, chatId, store.ProjectPath, store.SessionId));

        // Проверяем занятость до постановки в очередь, иначе первое же сообщение
        // может увидеть уже начавшуюся собственную обработку.
        var wasBusy = worker.IsBusy;

        // ActiveChatId выставляет ChatWorker перед самым запуском: сделать это здесь значило бы
        // увести карточки уже идущего запуска в чат другого пользователя.
        worker.Enqueue(chatId, userId, text);

        if (wasBusy)
            await bot.SendMessage(chatId, "📥 Добавлено в очередь — отвечу, как освобожусь.", cancellationToken: ct);

        return true;
    }
}
