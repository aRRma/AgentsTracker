namespace AgentsTracker.Agents;

/// <summary>Найденный исполняемый файл агента и его версия (null — узнать не удалось).</summary>
public sealed record AgentProbe(string Executable, string? Version);

/// <summary>
/// Агент, которого запускает шлюз: Claude Code, потом, может, Codex или Cursor. Хост знает
/// только этот контракт — как запускается процесс, чем отвечает и как просит разрешения,
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

    /// <summary>Один запуск на одно сообщение. Отмена <paramref name="ct"/> убивает процесс.</summary>
    Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentRunObserver observer, CancellationToken ct);
}
