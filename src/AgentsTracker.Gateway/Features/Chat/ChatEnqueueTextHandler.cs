using System.Text;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>
/// Последний текстовый обработчик: всё, что никто не забрал, — промпт агенту.
/// Регистрируется последним, поэтому всегда возвращает true.
/// </summary>
public sealed class ChatEnqueueTextHandler(
    IChatChannel channel, ChatWorker worker, AttachmentInbox inbox, SessionStore store, IAuditLog audit) : IChatTextHandler
{
    public async Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct)
    {
        var (chat, user) = (message.Chat, message.User);

        var attachments = await inbox.StoreAsync(chat, user, message.Attachments, ct);

        if (attachments.Refusal is { Length: > 0 } refusal)
            await channel.SendAsync(chat, new OutgoingMessage(refusal, Rich: false), ct);

        var text = Compose(message.Text, attachments.Paths);

        // Ни текста, ни принятых картинок — агенту идти не с чем, тариф на это не тратим.
        if (text.Length == 0) return true;

        // Превью, а не весь промпт: в него могли вставить токен или содержимое файла.
        audit.Write(AuditEvent.Now(AuditKinds.Message, $"text: {Text.Preview(text)}", user, chat, store.ProjectPath, store.SessionId));

        // Занятость проверяем до постановки в очередь: иначе сообщение увидит уже
        // начавшуюся собственную обработку.
        var wasBusy = worker.IsBusy;

        // Команды шлюза сюда не доходят, так что слэш здесь — команда или скилл агента.
        // Считаем запуски, чтобы экран скиллов знал, что зовут чаще.
        if (text.StartsWith('/')) store.RecordSkillUse(CommandName(text));

        // Активный чат ставит ChatWorker перед запуском: сделай это здесь — и карточки
        // идущего запуска уедут в чат другого пользователя.
        worker.Enqueue(chat, user, text);

        if (wasBusy)
            await channel.SendAsync(chat, new OutgoingMessage("📥 Добавлено в очередь — отвечу, как освобожусь.", Rich: false), ct);

        return true;
    }

    /// <summary>
    /// Подпись плюс пути к принятым картинкам. Путь без указания прочитать его так и остался
    /// бы строкой в промпте: агент видит картинку, только если сам вызовет Read.
    /// </summary>
    private static string Compose(string text, IReadOnlyList<string> images)
    {
        if (images.Count == 0) return text;

        var lines = new StringBuilder(text.Length > 0
            ? text
            : "Пользователь прислал изображение без комментария. Открой файл через Read и опиши, "
              + "что на нём, или спроси, что с ним делать.");

        lines.AppendLine().AppendLine();

        for (var i = 0; i < images.Count; i++)
        {
            var number = images.Count > 1 ? $" {i + 1}" : "";
            lines.AppendLine($"[Приложено изображение{number}: {images[i]}]");
        }

        return lines.ToString().TrimEnd();
    }

    /// <summary>Первое слово без «@имябота» — канал подставляет его при выборе из подсказок.</summary>
    private static string CommandName(string text)
    {
        var word = text.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var at = word.IndexOf('@');
        return at < 0 ? word : word[..at];
    }
}
