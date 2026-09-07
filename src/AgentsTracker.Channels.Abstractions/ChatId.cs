namespace AgentsTracker.Channels;

/// <summary>
/// Адрес чата в конкретном канале. У каждого канала своя версия (у Telegram — число),
/// хост видит только базовый тип и сравнивает адреса как значения. В state.json, аудит и
/// лог уходит <see cref="Key"/> — строка с именем канала, чтобы адреса разных каналов
/// не совпали случайно.
/// </summary>
public abstract record ChatId
{
    /// <summary>Строковая форма «канал:значение», например «telegram:123456789».</summary>
    public abstract string Key { get; }

    public sealed override string ToString() => Key;
}

/// <summary>Адрес пользователя в конкретном канале; устройство такое же, как у <see cref="ChatId"/>.</summary>
public abstract record UserId
{
    /// <summary>Строковая форма «канал:значение».</summary>
    public abstract string Key { get; }

    public sealed override string ToString() => Key;
}
