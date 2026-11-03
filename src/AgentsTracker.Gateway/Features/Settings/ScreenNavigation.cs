using System.Collections.Concurrent;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Где пользователь находится в двухуровневом списке: открытая группа, страница, карточка.</summary>
/// <param name="Group">Ключ открытой группы (<see cref="SettingsKeyboard.Key12"/>); null — список групп.</param>
/// <param name="Page">Номер страницы открытого списка.</param>
/// <param name="Card">Ключ открытой карточки элемента; null — список.</param>
internal sealed record ScreenPosition(string? Group = null, int Page = 0, string? Card = null);

/// <summary>
/// Позиция в списке на каждый чат. Экраны — синглтоны, а чатов много: у каждого разрешённого
/// свой, и у каждой темы свой. С общим полем страница, открытая в одном, подменила бы список
/// под пальцем в другом. Чаты только личные, так что чат — это и пользователь.
/// </summary>
internal sealed class ScreenNavigation
{
    private readonly ConcurrentDictionary<ChatId, ScreenPosition> _byChat = new();

    public ScreenPosition Of(ChatId chat) => _byChat.GetValueOrDefault(chat) ?? new ScreenPosition();

    public void Set(ChatId chat, ScreenPosition position) => _byChat[chat] = position;

    public void Update(ChatId chat, Func<ScreenPosition, ScreenPosition> change) => Set(chat, change(Of(chat)));

    /// <summary>К началу — при открытии экрана из корня или командой: иначе показалась бы прошлая карточка.</summary>
    public void Reset(ChatId chat) => _byChat.TryRemove(chat, out _);
}
