namespace AgentsTracker.Gateway.Domain;

/// <summary>
/// Сколько модели думать над задачей — то, что уходит в <c>--effort</c>.
/// Порядок в <see cref="All"/> — от дешёвого к дорогому, на нём строится клавиатура меню.
/// </summary>
public static class EffortLevels
{
    public static readonly string[] All = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>Каноническое имя уровня по тому, что написал пользователь. null — не узнали.</summary>
    public static string? Resolve(string value)
    {
        var trimmed = value.Trim();
        return All.FirstOrDefault(level => level.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static string Describe(string level) => level switch
    {
        "low" => "low — отвечает быстро, почти не рассуждает",
        "medium" => "medium — баланс скорости и качества",
        "high" => "high — думает дольше, лучше на сложных задачах",
        "xhigh" => "xhigh — думает очень долго",
        "max" => "max — максимум рассуждений и стоимости",
        _ => level,
    };
}
