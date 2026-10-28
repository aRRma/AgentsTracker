using System.Collections.Concurrent;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Второе нажатие для необратимых кнопок меню: первое только спрашивает «точно?», выполняет
/// второе. Ожидание помнит, о чём спрашивали и в какой сессии: кнопка «Да» из старого
/// сообщения или после смены сессии не должна сработать по другой ветке.
/// </summary>
internal sealed class PendingConfirmations
{
    /// <summary>
    /// Сколько живёт вопрос. Дольше — и «Да» в забытом меню выполнил бы то, о чём
    /// пользователь уже не помнит.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private sealed record Pending(string Action, string? Scope, DateTimeOffset AskedUtc);

    // По чату, а не общим полем: экраны — синглтоны, а чатов много — у каждого разрешённого и
    // у каждой темы. Вопрос из одной темы не должен всплыть в меню другой.
    private readonly ConcurrentDictionary<ChatId, Pending> _byChat = new();

    public void Ask(ChatId chat, string action, string? scope) =>
        _byChat[chat] = new Pending(action, scope, DateTimeOffset.UtcNow);

    /// <summary>О чём сейчас спрашиваем в чате; null — ни о чём или вопрос устарел.</summary>
    public string? Asked(ChatId chat) =>
        _byChat.TryGetValue(chat, out var pending) && Fresh(pending) ? pending.Action : null;

    /// <summary>
    /// Снимает вопрос и говорит, можно ли выполнять: то же действие, та же сессия и не позже
    /// <see cref="Lifetime"/>. Снимаем в любом случае — повторное «Да» не должно сработать.
    /// </summary>
    public bool TryConfirm(ChatId chat, string action, string? scope) =>
        _byChat.TryRemove(chat, out var pending)
        && pending.Action == action
        && string.Equals(pending.Scope, scope, StringComparison.Ordinal)
        && Fresh(pending);

    public void Cancel(ChatId chat) => _byChat.TryRemove(chat, out _);

    private static bool Fresh(Pending pending) => DateTimeOffset.UtcNow - pending.AskedUtc <= Lifetime;
}
