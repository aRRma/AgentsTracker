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
        // Превью, а не весь промпт: в него могли вставить токен или содержимое файла.
        audit.Write(AuditEvent.Now(AuditKinds.Message, $"text: {Text.Preview(text)}", userId, chatId, store.ProjectPath, store.SessionId));

        // Проверяем занятость до постановки в очередь, иначе первое же сообщение
        // может увидеть уже начавшуюся собственную обработку.
        var wasBusy = worker.IsBusy;

        // Слэш-команды шлюза сюда не доходят — остались команды и скиллы самого агента.
        // Считаем их, чтобы экран скиллов знал, что запускают чаще всего.
        if (text.StartsWith('/')) store.RecordSkillUse(CommandName(text));

        // ActiveChatId выставляет ChatWorker перед самым запуском: сделать это здесь значило бы
        // увести карточки уже идущего запуска в чат другого пользователя.
        worker.Enqueue(chatId, userId, text);

        if (wasBusy)
            await bot.SendMessage(chatId, "📥 Добавлено в очередь — отвечу, как освобожусь.", cancellationToken: ct);

        return true;
    }

    /// <summary>Первое слово без суффикса «@имябота»: Telegram подставляет его при выборе из подсказок.</summary>
    private static string CommandName(string text)
    {
        var word = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var at = word.IndexOf('@');
        return at < 0 ? word : word[..at];
    }
}
