using System.Collections.Concurrent;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Features.Question;

/// <summary>
/// Вопрос вне сессии: ставит его в очередь вопросов и помнит, кто нажал «Вопрос» и ещё не
/// написал текст. Очередь своя, не задач: вопрос задают, чтобы не ждать идущую задачу.
/// Карточки двух запусков шлюз различает по id запуска, лимит проверяется перед каждым.
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

    /// <param name="message">Сообщение с вопросом: ответ придёт ответом на него.</param>
    public string Ask(ChatId chat, UserId user, string text, MessageRef? message)
    {
        if (Refusal is { } refusal) return refusal;

        // Превью, а не весь вопрос: в него могли вставить токен или содержимое файла.
        audit.Write(AuditEvent.Now(AuditKinds.Message, $"question: {Text.Preview(text)}", user, chat, store.ProjectPath));

        // Ждать он будет только другой вопрос: задача ему не помеха.
        var waits = worker.IsAnswering || worker.QuestionQueueLength > 0;
        worker.Enqueue(chat, user, text, AgentRunKind.Question, message);
        return waits
            ? "📥 Вопрос в очереди за предыдущим вопросом — отвечу, как освобожусь."
            : "💬 Вопрос принят — отвечу вне сессии, не дожидаясь задачи.";
    }

    public void Expect(UserId user) => _pending[user] = DateTimeOffset.UtcNow;

    public bool TryTake(UserId user) =>
        _pending.TryRemove(user, out var since) && DateTimeOffset.UtcNow - since <= Wait;

    public void Cancel(UserId user) => _pending.TryRemove(user, out _);
}
