namespace AgentsTracker.Agents;

/// <summary>Найденный исполняемый файл агента и его версия (null — узнать не удалось).</summary>
public sealed record AgentProbe(string Executable, string? Version);

/// <summary>
/// Агент, которого запускает шлюз: Claude Code, а в будущем Codex или Cursor. Хост знает
/// только этот контракт — как запускается процесс, в каком формате отвечает и как просит
/// разрешения, остаётся внутри бэкенда.
/// </summary>
public interface IAgentBackend
{
    /// <summary>Ключ для конфига (<c>Gateway:Agent</c>) и журнала: «claude».</summary>
    string Id { get; }

    /// <summary>Имя для людей: «Claude Code».</summary>
    string DisplayName { get; }

    AgentCapabilities Capabilities { get; }

    /// <summary>
    /// Находит исполняемый файл и спрашивает версию. Зовётся при старте шлюза и не должен
    /// кэшировать результат намертво: бинарник может пропасть, пока шлюз живёт сутками.
    /// </summary>
    /// <exception cref="InvalidOperationException">Агент не найден — запускаться нельзя, текст для лога.</exception>
    AgentProbe Probe();

    /// <summary>Один запуск на одно сообщение. Отмена <paramref name="ct"/> убивает процесс.</summary>
    Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentRunObserver observer, CancellationToken ct);
}
