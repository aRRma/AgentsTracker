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

    // По пользователю, а не общим полем: экраны — синглтоны, а разрешённых бывает несколько.
    private readonly ConcurrentDictionary<UserId, Pending> _byUser = new();

    public void Ask(UserId user, string action, string? scope) =>
        _byUser[user] = new Pending(action, scope, DateTimeOffset.UtcNow);

    /// <summary>О чём сейчас спрашиваем пользователя; null — ни о чём или вопрос устарел.</summary>
    public string? Asked(UserId user) =>
        _byUser.TryGetValue(user, out var pending) && Fresh(pending) ? pending.Action : null;

    /// <summary>
    /// Снимает вопрос и говорит, можно ли выполнять: то же действие, та же сессия и не позже
    /// <see cref="Lifetime"/>. Снимаем в любом случае — повторное «Да» не должно сработать.
    /// </summary>
    public bool TryConfirm(UserId user, string action, string? scope) =>
        _byUser.TryRemove(user, out var pending)
        && pending.Action == action
        && string.Equals(pending.Scope, scope, StringComparison.Ordinal)
        && Fresh(pending);

    public void Cancel(UserId user) => _byUser.TryRemove(user, out _);

    private static bool Fresh(Pending pending) => DateTimeOffset.UtcNow - pending.AskedUtc <= Lifetime;
}
