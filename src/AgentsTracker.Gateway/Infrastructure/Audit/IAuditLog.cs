namespace AgentsTracker.Gateway.Infrastructure.Audit;

/// <summary>Журнал действий, переживающий перезапуск и не тонущий в отладочном логе.</summary>
public interface IAuditLog
{
    void Write(AuditEvent entry);

    /// <summary>Последние записи, свежие снизу.</summary>
    IReadOnlyList<AuditEvent> Tail(int count);
}
