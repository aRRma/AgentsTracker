using System.Collections.Concurrent;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Где пользователь находится в двухуровневом списке: открытая группа, страница, карточка.</summary>
/// <param name="Group">Ключ открытой группы (<see cref="SettingsKeyboard.Key12"/>); null — список групп.</param>
/// <param name="Page">Номер страницы открытого списка.</param>
/// <param name="Card">Ключ открытой карточки элемента; null — список.</param>
internal sealed record ScreenPosition(string? Group = null, int Page = 0, string? Card = null);

/// <summary>
/// Позиция в списке на каждого пользователя. Экраны — синглтоны, а разрешённых пользователей
/// у канала бывает несколько: общее поле означало бы, что страница, открытая одним,
/// подменяет список под пальцем у другого.
/// </summary>
internal sealed class ScreenNavigation
{
    private readonly ConcurrentDictionary<UserId, ScreenPosition> _byUser = new();

    public ScreenPosition Of(UserId user) => _byUser.GetValueOrDefault(user) ?? new ScreenPosition();

    public void Set(UserId user, ScreenPosition position) => _byUser[user] = position;

    public void Update(UserId user, Func<ScreenPosition, ScreenPosition> change) => Set(user, change(Of(user)));

    /// <summary>К началу: вызывается при открытии экрана из корня или командой, иначе показалась бы прошлая карточка.</summary>
    public void Reset(UserId user) => _byUser.TryRemove(user, out _);
}
