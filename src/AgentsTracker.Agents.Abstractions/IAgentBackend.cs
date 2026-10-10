namespace AgentsTracker.Agents;

/// <summary>Найденный исполняемый файл агента и его версия (null — узнать не удалось).</summary>
public sealed record AgentProbe(string Executable, string? Version);

/// <summary>
/// Настройки агента в проекте — то, что агент выполнит сам, без карточек подтверждения:
/// хуки, MCP-серверы, правила «всегда», скиллы. Хост спрашивает доверие к проекту по
/// <see cref="Items"/> и помнит его по <see cref="Fingerprint"/>.
/// </summary>
/// <param name="Fingerprint">Меняется с любым из этих файлов: изменённые настройки — новый вопрос.</param>
/// <param name="Items">Что в них есть, строками для человека; без значений секретов.</param>
public sealed record ProjectSettings(string Fingerprint, IReadOnlyList<string> Items);

/// <summary>
/// Агент, которого запускает шлюз: Claude Code, в будущем Codex или Cursor. Хост знает
/// только этот контракт — как запускается процесс, чем отвечает и как просит подтверждения,
/// остаётся внутри бэкенда.
/// </summary>
public interface IAgentBackend
{
    /// <summary>Ключ для конфига (<c>Gateway:Agent</c>) и журнала: «claude».</summary>
    string Id { get; }

    /// <summary>Имя для людей: «Claude Code».</summary>
    string DisplayName { get; }

    AgentCapabilities Capabilities { get; }

    /// <summary>
    /// Находит исполняемый файл и спрашивает версию. Зовётся при старте; кэшировать результат
    /// намертво нельзя — бинарник может пропасть, пока шлюз живёт сутками.
    /// </summary>
    /// <exception cref="InvalidOperationException">Агента нет — запускаться нельзя, текст в лог.</exception>
    AgentProbe Probe();

    /// <summary>
    /// Настройки агента в проекте; null — их нет, спрашивать нечего. Агент в режиме без
    /// человека за терминалом не спрашивает сам, доверять ли проекту, и чужой проект выполнил
    /// бы свои хуки молча — поэтому спрашивает хост, до запуска. Не бросает: нечитаемый файл
    /// тоже пункт, а не повод пропустить проверку.
    /// </summary>
    ProjectSettings? InspectProject(string projectPath);

    /// <summary>Один запуск на одно сообщение. Отмена <paramref name="ct"/> убивает процесс.</summary>
    Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentRunObserver observer, CancellationToken ct);
}
