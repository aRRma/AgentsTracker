namespace AgentsTracker.Gateway.Domain;

/// <summary>Виды записей аудита. Строки, а не enum: журнал читают глазами и grep-ом.</summary>
public static class AuditKinds
{
    public const string AccessRejected = "access.rejected";
    public const string Message = "message";
    public const string RunStart = "run.start";
    public const string RunEnd = "run.end";
    public const string Approval = "approval";
    public const string Question = "question";
    public const string Settings = "settings";
    public const string Rules = "rules";
    public const string SessionReset = "session.reset";
    public const string BudgetRefused = "budget.refused";
    public const string Gateway = "gateway";
}

/// <summary>
/// Одна короткая запись журнала действий: кто пришёл (UserId/ChatId), куда (Project/Session)
/// и что сделал (Kind/Summary/Outcome). Никаких секретов и полных текстов — только суть.
/// </summary>
public sealed record AuditEvent(
    DateTimeOffset At,
    string Kind,
    string Summary,
    long? UserId = null,
    long? ChatId = null,
    string? Project = null,
    string? Session = null,
    string? Outcome = null)
{
    /// <summary>Сколько символов Summary держать: строка должна читаться в чате одной строкой.</summary>
    public const int SummaryLimit = 200;

    public static AuditEvent Now(
        string kind, string summary, long? userId = null, long? chatId = null,
        string? project = null, string? session = null, string? outcome = null) =>
        new(DateTimeOffset.Now, kind, Text.Clip(summary.ReplaceLineEndings(" "), SummaryLimit), userId, chatId,
            project is { Length: > 0 } ? Path.GetFileName(project.TrimEnd('\\', '/')) : null,
            session is { Length: > 8 } ? session[..8] : session,
            outcome);
}
