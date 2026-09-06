namespace AgentsTracker.Agents;

/// <summary>
/// Настройка агента с перечислимыми значениями: модель, effort, режим разрешений. Экраны
/// меню строятся по ней, а не по своим спискам — иначе каждый новый агент переписывал бы меню.
/// </summary>
/// <param name="Values">Все допустимые канонические значения в порядке показа (для кнопок и проверки конфига).</param>
/// <param name="Selectable">
/// Что можно выбрать из чата. Подмножество <paramref name="Values"/>: у режима разрешений
/// сюда не входят режимы без подтверждений — снять их можно только конфигом на самой машине.
/// </param>
/// <param name="Resolve">Каноническое значение по тому, что написал пользователь (алиасы, регистр); null — не узнали.</param>
/// <param name="Describe">Человеческое описание значения для экрана.</param>
public sealed record AgentSetting(
    IReadOnlyList<string> Values,
    IReadOnlyList<string> Selectable,
    Func<string, string?> Resolve,
    Func<string, string> Describe)
{
    public bool IsValid(string value) => Values.Contains(value, StringComparer.Ordinal);

    public bool IsSelectable(string value) => Selectable.Contains(value, StringComparer.Ordinal);
}

/// <summary>Что агент умеет и какие настройки принимает. Хост прячет кнопки того, чего нет.</summary>
/// <param name="Model">
/// Модель. <see cref="AgentSetting.Values"/> — алиасы для кнопок; полное имя модели
/// (<c>claude-sonnet-5</c>) вводится текстом и проходит через <see cref="AgentSetting.Resolve"/>,
/// который для модели принимает любую непустую строку.
/// </param>
/// <param name="Effort">Уровень усилий; null — агент такого не умеет, экран и команда скрыты.</param>
/// <param name="PermissionMode">Режим разрешений — обязателен: без него хост не знает, что писать в конфиг по умолчанию.</param>
public sealed record AgentCapabilities(
    AgentSetting Model,
    AgentSetting? Effort,
    AgentSetting PermissionMode);
