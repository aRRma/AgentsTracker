using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Если агент попросил свободный текст (причина отказа, свой вариант ответа) — отдаём туда.
/// Регистрируется раньше постановки в очередь, иначе ответ ушёл бы новым промптом.
/// </summary>
public sealed class ApprovalTextHandler(ApprovalBroker broker, IChatChannel channel) : IChatTextHandler
{
    public async Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct)
    {
        if (!broker.IsAwaitingText(message.Chat)) return false;

        var delivery = message.Text.Length > 0
            ? broker.DeliverText(message.Chat, message.InReplyTo, message.Text)
            : TextDelivery.NotAwaited;

        // Ждут сразу несколько (задача и вопрос, параллельные сабагенты), а сообщение не ответ
        // ни на одну просьбу. Угадывать нельзя — причина ушла бы не той карточке; пропустить
        // дальше тоже нельзя — текст стал бы новой задачей.
        if (delivery == TextDelivery.Ambiguous)
        {
            await channel.SendAsync(message.Chat, new OutgoingMessage(
                "⚠️ Текста ждут сразу несколько карточек — ответьте (reply) на сообщение той, которой он адресован.",
                Rich: false), ct);
            return true;
        }

        // Картинку агенту в ответ на карточку не передать — он ждёт строку. Пропустить
        // сообщение дальше тоже нельзя: карточка осталась бы висеть, а запуск — стоять,
        // поэтому говорим вслух, что с картинкой ничего не сделано.
        if (message.Attachments.Count > 0)
        {
            var reply = message.Text.Length > 0
                ? "⚠️ Картинка в ответ на карточку не идёт — принята только подпись."
                : "⚠️ Карточка ждёт текстового ответа — напишите его сообщением.";

            await channel.SendAsync(message.Chat, new OutgoingMessage(reply, Rich: false), ct);
        }

        return message.Text.Length == 0 || delivery == TextDelivery.Delivered;
    }
}
