using AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

namespace AgentsTracker.Gateway.Features.Approvals;

/// <summary>
/// Если агент попросил свободный текст (причина отказа, свой вариант ответа) — отдаём туда.
/// Регистрируется раньше постановки в очередь, иначе ответ ушёл бы новым промптом.
/// </summary>
public sealed class ApprovalTextHandler(ApprovalBroker broker) : IChatTextHandler
{
    public Task<bool> TryHandleAsync(ChatId chat, UserId user, string text, CancellationToken ct) =>
        Task.FromResult(broker.TryConsumeText(chat, text));
}
