namespace AgentsTracker.Agents.Claude;

/// <summary>Секция <c>Gateway:Claude</c> — то, что нужно только этому агенту.</summary>
public sealed class ClaudeOptions
{
    public const string SectionName = "Gateway:Claude";

    /// <summary>Путь к claude.exe. null = автопоиск (см. <see cref="ClaudeCliLocator"/>).</summary>
    public string? Executable { get; set; }

    /// <summary>
    /// Встроенные скиллы Claude Code для экрана «Скиллы»: они вшиты в claude.exe, на диске их
    /// нет и перечислить их CLI не умеет. Формат строки — «/команда | описание | подсказка
    /// аргументов» (третья часть необязательна). Список по умолчанию соответствует CLI 2.1.x;
    /// после обновления CLI его можно переопределить здесь, не пересобирая шлюз.
    /// </summary>
    public string[] BuiltInSkills { get; set; } =
    [
        "/code-review | Ревью текущего диффа или PR: баги, упрощения, эффективность. Уровень low/medium — меньше находок, но точнее; high…max — шире. --fix применяет правки после ревью, --comment пишет замечания в PR. | [low|medium|high|xhigh|max] [номер PR|ветка|путь] [--fix] [--comment]",
        "/simplify | Упростить изменённый код: повторное использование, лишние абстракции, эффективность. Правки применяются сразу.",
        "/security-review | Проверка безопасности изменений в текущей ветке.",
        "/init | Создать CLAUDE.md с описанием кодовой базы.",
        "/fewer-permission-prompts | Разобрать историю и разрешить частые безопасные команды в .claude/settings.json, чтобы меньше спрашивать.",
    ];
}
