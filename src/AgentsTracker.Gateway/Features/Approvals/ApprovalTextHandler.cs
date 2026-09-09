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

        return message.Text.Length == 0 || broker.TryConsumeText(message.Chat, message.Text);
    }
}
