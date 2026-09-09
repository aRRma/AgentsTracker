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
    public const string FileSend = "file.send";
    public const string FileReceive = "file.receive";
    public const string Settings = "settings";
    public const string Rules = "rules";
    public const string SessionReset = "session.reset";
    public const string LimitRefused = "limit.refused";
    public const string Gateway = "gateway";
}

/// <summary>
/// Одна запись журнала: кто (UserKey/ChatKey), куда (Project/Session) и что сделал
/// (Kind/Summary/Outcome). Без секретов и полных текстов. Адреса строками «канал:значение»:
/// номера разных каналов совпадают, и число из одного читалось бы как чужой пользователь.
/// </summary>
public sealed record AuditEvent(
    DateTimeOffset At,
    string Kind,
    string Summary,
    string? UserKey = null,
    string? ChatKey = null,
    string? Project = null,
    string? Session = null,
    string? Outcome = null)
{
    /// <summary>Предел Summary: запись должна укладываться в одну строку в чате.</summary>
    public const int SummaryLimit = 200;

    public static AuditEvent Now(
        string kind, string summary, UserId? user = null, ChatId? chat = null,
        string? project = null, string? session = null, string? outcome = null) =>
        NowByKeys(kind, summary, user?.Key, chat?.Key, project, session, outcome);

    /// <summary>
    /// То же, но адреса уже строками — для записи по данным из state.json, где лежат
    /// готовые Key (прерванный запуск прошлого экземпляра).
    /// </summary>
    public static AuditEvent NowByKeys(
        string kind, string summary, string? userKey = null, string? chatKey = null,
        string? project = null, string? session = null, string? outcome = null) =>
        new(DateTimeOffset.Now, kind, Text.Clip(summary.ReplaceLineEndings(" "), SummaryLimit), userKey, chatKey,
            project is { Length: > 0 } ? Path.GetFileName(project.TrimEnd('\\', '/')) : null,
            session is { Length: > 8 } ? session[..8] : session,
            outcome);
}
