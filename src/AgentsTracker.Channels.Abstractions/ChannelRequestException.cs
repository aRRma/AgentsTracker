namespace AgentsTracker.Channels;

/// <summary>Почему канал не выполнил запрос — то, на что хост реагирует по-разному.</summary>
public enum ChannelFailure
{
    Unknown,

    /// <summary>Канал просит подождать; срок — в <see cref="ChannelRequestException.RetryAfter"/>.</summary>
    RateLimited,

    /// <summary>Канал отверг разметку текста.</summary>
    MarkupRejected,

    /// <summary>Получатель недоступен: например, бот не может написать первым.</summary>
    CannotReach,
}

/// <summary>
/// Единственное исключение, которое канал показывает хосту: свои ошибки транспорта он
/// переводит сюда, чтобы фичи не знали ни одного типа конкретного мессенджера. Штатные
/// исходы («правка ничего не изменила») канал глотает сам и сюда не доводит.
/// </summary>
public sealed class ChannelRequestException(
    string message,
    ChannelFailure kind = ChannelFailure.Unknown,
    TimeSpan? retryAfter = null,
    Exception? inner = null) : Exception(message, inner)
{
    public ChannelFailure Kind { get; } = kind;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
