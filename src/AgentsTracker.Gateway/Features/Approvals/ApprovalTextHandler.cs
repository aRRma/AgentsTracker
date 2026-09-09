using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Если агент попросил свободный текст (причина отказа, свой вариант ответа) — отдаём туда.
/// Регистрируется раньше постановки в очередь, иначе ответ ушёл бы новым промптом.
/// </summary>
public sealed class ApprovalTextHandler(ApprovalBroker broker) : IChatTextHandler
{
    // Вложения агенту как ответ на карточку не передаём: он ждёт строку.
    public Task<bool> TryHandleAsync(IncomingMessage message, CancellationToken ct) =>
        Task.FromResult(message.Text.Length > 0 && broker.TryConsumeText(message.Chat, message.Text));
}
