using AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Если агент попросил свободный текст (причина отказа, свой вариант ответа) — отдаём туда.
/// Регистрируется раньше постановки в очередь, иначе ответ ушёл бы новым промптом.
/// </summary>
public sealed class ApprovalTextHandler(ApprovalBroker broker) : ITelegramTextHandler
{
    public Task<bool> TryHandleAsync(long chatId, long userId, string text, CancellationToken ct) =>
        Task.FromResult(broker.TryConsumeText(chatId, text));
}
