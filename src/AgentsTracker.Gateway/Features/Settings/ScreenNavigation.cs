using System.Collections.Concurrent;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Где пользователь находится в двухуровневом списке: открытая группа, страница, карточка.</summary>
/// <param name="Group">Ключ открытой группы (<see cref="SettingsKeyboard.Key12"/>); null — список групп.</param>
/// <param name="Page">Номер страницы открытого списка.</param>
/// <param name="Card">Ключ открытой карточки элемента; null — список.</param>
internal sealed record ScreenPosition(string? Group = null, int Page = 0, string? Card = null);

/// <summary>
/// Позиция в списке на каждого пользователя. Экраны — синглтоны, а <c>AllowedUserIds</c>
/// допускает нескольких людей: общее поле означало бы, что страница, открытая одним,
/// подменяет список под пальцем у другого.
/// </summary>
internal sealed class ScreenNavigation
{
    private readonly ConcurrentDictionary<long, ScreenPosition> _byUser = new();

    public ScreenPosition Of(long userId) => _byUser.GetValueOrDefault(userId) ?? new ScreenPosition();

    public void Set(long userId, ScreenPosition position) => _byUser[userId] = position;

    public void Update(long userId, Func<ScreenPosition, ScreenPosition> change) => Set(userId, change(Of(userId)));

    /// <summary>К началу: вызывается при открытии экрана из корня или командой, иначе показалась бы прошлая карточка.</summary>
    public void Reset(long userId) => _byUser.TryRemove(userId, out _);
}
