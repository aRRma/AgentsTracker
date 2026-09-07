using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>Запись «настройка изменена» в одном формате для всех экранов и команд.</summary>
internal static class SettingsAudit
{
    extension(IAuditLog audit)
    {
        public void Changed(SessionStore store, UserId user, string what, string? from, string? to) =>
            audit.Write(AuditEvent.Now(
                AuditKinds.Settings, $"{what}: {from ?? "—"} → {to ?? "—"}",
                user, project: store.ProjectPath, session: store.SessionId));
    }
}
