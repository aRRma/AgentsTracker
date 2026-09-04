using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Запись «настройка изменена» в одном формате для всех экранов и команд.</summary>
internal static class SettingsAudit
{
    extension(IAuditLog audit)
    {
        public void Changed(SessionStore store, long userId, string what, string? from, string? to) =>
            audit.Write(AuditEvent.Now(
                AuditKinds.Settings, $"{what}: {from ?? "—"} → {to ?? "—"}",
                userId, project: store.ProjectPath, session: store.SessionId));
    }
}
