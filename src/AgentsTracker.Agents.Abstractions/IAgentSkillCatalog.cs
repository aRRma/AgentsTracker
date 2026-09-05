namespace AgentsTracker.Agents;

/// <summary>Один скилл (или пользовательская команда) агента: что набрать и что оно делает.</summary>
/// <param name="Command">Полная слэш-команда: <c>/name</c> или <c>/plugin:name</c>.</param>
/// <param name="Description">Первое предложение описания — для списка.</param>
/// <param name="Details">Описание целиком (в разумных пределах) — для карточки скилла.</param>
/// <param name="Group">Откуда скилл: «Проект», «Личные» или имя плагина.</param>
/// <param name="ArgumentHint">Подсказка по аргументам; null — подсказки нет.</param>
/// <param name="Flags">Флаги вида <c>--name</c>, упомянутые в тексте скилла; пусто — не нашлось.</param>
public sealed record SkillInfo(
    string Command,
    string Description,
    string Details,
    string Group,
    string? ArgumentHint,
    IReadOnlyList<string> Flags);

/// <summary>Группа скиллов с общим источником: папка проекта, личная папка или плагин.</summary>
public sealed record SkillGroup(string Name, IReadOnlyList<SkillInfo> Skills);

/// <summary>
/// Слэш-команды агента, которые можно запустить из чата. Хост показывает их кнопками и
/// кладёт выбранную в очередь как обычный текст: агент сам разбирает свои команды.
/// </summary>
public interface IAgentSkillCatalog
{
    /// <summary>Группы в порядке показа. Зовётся на каждое нажатие в меню — кэшируйте обход диска.</summary>
    IReadOnlyList<SkillGroup> Grouped(string projectPath);

    /// <summary>Подсказка для пустого списка: где агент ищет скиллы, чтобы пользователь знал, куда их класть.</summary>
    string EmptyHint { get; }
}

/// <summary>Для агента без скиллов: список пуст, экран прячет кнопку.</summary>
public sealed class NoAgentSkills : IAgentSkillCatalog
{
    public IReadOnlyList<SkillGroup> Grouped(string projectPath) => [];

    public string EmptyHint => "У этого агента нет слэш-команд.";
}
