using System.Collections.Concurrent;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Features.Question;

/// <summary>
/// Вопрос вне сессии: ставит его в очередь и помнит, кто нажал «Вопрос» и ещё не написал
/// текст. Очередь та же, что у задач: второй процесс агента рядом с идущим запуском делил бы
/// с ним лимит тарифа и карточки подтверждения.
/// </summary>
public sealed class QuestionLauncher(ChatWorker worker, SessionStore store, IAgentBackend agent, IAuditLog audit)
{
    /// <summary>
    /// Сколько ждать текст вопроса. Дольше — и сообщение, написанное через час как задача,
    /// ушло бы мимо сессии.
    /// </summary>
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<UserId, DateTimeOffset> _pending = new();

    /// <summary>Почему вопрос задать нельзя; null — можно.</summary>
    public string? Refusal =>
        agent.Capabilities.Questions ? null : $"{agent.DisplayName} не умеет отвечать вне сессии.";

    public string Ask(ChatId chat, UserId user, string text)
    {
        if (Refusal is { } refusal) return refusal;

        // Превью, а не весь вопрос: в него могли вставить токен или содержимое файла.
        audit.Write(AuditEvent.Now(AuditKinds.Message, $"question: {Text.Preview(text)}", user, chat, store.ProjectPath));

        var wasBusy = worker.IsBusy;
        worker.Enqueue(chat, user, text, AgentRunKind.Question);
        return wasBusy ? "📥 Вопрос в очереди — отвечу, как освобожусь." : "💬 Вопрос принят — отвечу вне сессии.";
    }

    public void Expect(UserId user) => _pending[user] = DateTimeOffset.UtcNow;

    public bool TryTake(UserId user) =>
        _pending.TryRemove(user, out var since) && DateTimeOffset.UtcNow - since <= Wait;

    public void Cancel(UserId user) => _pending.TryRemove(user, out _);
}
