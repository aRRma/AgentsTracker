using System.Globalization;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Контекст активной сессии: насколько заполнен, из чего состоит, сжать, начать новую.
/// Заполненность — замер после последнего запуска, без вызова агента: экран открывают, чтобы
/// решить, не пора ли сжать, и платить за это запуском было бы странно. «Сжать» и «Новая
/// сессия» необратимы, поэтому идут через подтверждение.
/// </summary>
public sealed class ContextScreen(
    SessionStore store, IAgentBackend agent, ChatWorker worker, AttachmentInbox inbox, IAuditLog audit) : ISettingsScreen
{
    private const string NewSession = "new";
    private const string Compact = "compact";

    private readonly PendingConfirmations _confirmations = new();

    public string Key => "ctx";

    public void Open(UserId user) => _confirmations.Cancel(user);

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        var session = store.SessionId;

        switch (argument)
        {
            case "info":
                if (session is null) return "Активной сессии нет — контекст пуст";
                return Enqueue(chat, user, AgentRunKind.ContextReport, "контекст: подробно",
                    "📋 Разбивка придёт отдельным сообщением");

            case NewSession or Compact:
                if (session is null) return "Активной сессии нет — следующее сообщение и так начнёт новую";
                if (argument == Compact && worker.IsBusy) return "Идёт запуск — сжать можно, когда он закончится";

                // Сессию запоминаем вместе с вопросом: если её сменят до «Да», подтверждение
                // не сработает по чужой ветке.
                _confirmations.Ask(user, argument, session);
                return null;

            case "no":
                _confirmations.Cancel(user);
                return null;

            case "yes:" + NewSession:
                if (session is null || !_confirmations.TryConfirm(user, NewSession, session)) return Stale;

                store.SetSessionId(null);
                // Как и /new: прошлый контекст забыт, картинки к нему больше не нужны.
                inbox.ClearSession(chat, store.ProjectPath);
                audit.Changed(store, user, "session", session.ShortId, "новая");
                return "🆕 Следующее сообщение начнёт новую сессию";

            case "yes:" + Compact:
                if (!_confirmations.TryConfirm(user, Compact, session)) return Stale;
                if (worker.IsBusy) return "Идёт запуск — сжать можно, когда он закончится";

                return Enqueue(chat, user, AgentRunKind.Compact, "контекст: сжать",
                    "🗜 Сжатие запущено — итог придёт сообщением");

            default:
                return null;
        }
    }

    private const string Stale = "Подтверждение устарело — нажмите кнопку ещё раз";

    private string Enqueue(ChatId chat, UserId user, AgentRunKind kind, string label, string reply)
    {
        audit.Write(AuditEvent.Now(AuditKinds.Message, label, user, chat, store.ProjectPath, store.SessionId));

        // Занятость проверяется до постановки в очередь — как в ChatEnqueueTextHandler.
        var wasBusy = worker.IsBusy;
        worker.Enqueue(chat, user, label, kind);
        return wasBusy ? "📥 В очереди — отвечу, как освобожусь" : reply;
    }

    public Task<(string Html, Keyboard Keyboard)> RenderAsync(UserId user, CancellationToken ct) =>
        Task.FromResult(Render(user));

    private (string Html, Keyboard Keyboard) Render(UserId user)
    {
        var project = store.ProjectPath;
        var active = store.SessionId;
        var session = active is null
            ? null
            : store.SessionsFor(project).FirstOrDefault(s => string.Equals(s.Id, active, StringComparison.Ordinal));

        if (active is not null && _confirmations.Asked(user) is { } asked)
            return Confirmation(asked, session);

        var title = $"📦 <b>Контекст</b> — {E(Path.GetFileName(project))}";

        if (active is null)
        {
            return ($"""
                {title}

                ⚪ Активной сессии нет — контекст пуст. Следующее сообщение начнёт новую сессию.
                """, new Keyboard([[BackButton]]));
        }

        var name = session is null ? active.ShortId : session.Title;

        var html = $"""
            {title}

            🧵 Сессия: <b>{E(name)}</b>
            {Fill(session)}

            <i>«Подробно» — из чего состоит контекст. «Сжать» — агент перескажет разговор кратко, сессия останется та же. «Новая сессия» — начать с чистого листа.</i>
            """;

        var rows = new List<KeyboardButton[]>();
        if (agent.Capabilities.Context)
            rows.Add([Button("📋 Подробно", "ctx:info"), Button("🗜 Сжать", $"ctx:{Compact}")]);
        rows.Add([Button("🆕 Новая сессия", $"ctx:{NewSession}")]);
        rows.Add([BackButton]);

        return (html, new Keyboard(rows));
    }

    /// <summary>Строка заполненности; для корневого экрана — без полосы.</summary>
    internal static string Fill(SessionRecord? session, bool bar = true)
    {
        if (session is null || Share(session) is not { } used)
            return "<i>Заполненность станет видна после следующего запуска в этой сессии.</i>";

        var line = $"{Lamp(used)} {Percent(used)}% · {session.ContextTokens.Tokens} из {session.ContextWindow.Tokens} токенов";

        if (!bar) return line;

        var measured = session.ContextUtc is { } at ? $"\n<i>Замер после запуска {E(at.Ago)}.</i>" : "";
        return $"<code>{LimitBars.Bar(used)}</code> {line}{measured}";
    }

    /// <summary>Лампа и процент для строки списка; пусто, пока замера не было.</summary>
    internal static string Short(SessionRecord session) =>
        Share(session) is { } used ? $"{Lamp(used)} {Percent(used)}%" : "";

    /// <summary>Доля занятого окна; null — замера ещё не было.</summary>
    private static decimal? Share(SessionRecord session) =>
        session is { ContextTokens: > 0, ContextWindow: > 0 }
            ? Math.Clamp(session.ContextTokens / (decimal)session.ContextWindow, 0m, 1m)
            : null;

    private static string Percent(decimal used) => LimitMath.Percent(used).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Когда пора сжимать. Пороги грубые: у Claude Code автоматическое сжатие срабатывает
    /// ближе к концу окна, и к 80% лучше сжать самому — с понятным пересказом, а не посреди задачи.
    /// </summary>
    private static string Lamp(decimal used) => used switch
    {
        < 0.5m => "🟢",
        < 0.8m => "🟡",
        _ => "🔴",
    };

    private static (string Html, Keyboard Keyboard) Confirmation(string action, SessionRecord? session)
    {
        var fill = session is not null && Share(session) is { } used ? $" (заполнен на {Percent(used)}%)" : "";
        var name = session is null ? "" : $" «{E(session.Title)}»";

        var html = action == Compact
            ? $"""
                🗜 <b>Сжать контекст?</b>

                Агент перескажет разговор сессии{name}{fill} кратко: место освободится, но мелкие детали могут потеряться. Сессия останется та же.
                Сжатие — это запуск, он расходует лимит тарифа.
                """
            : $"""
                🆕 <b>Начать новую сессию?</b>

                Контекст сессии{name}{fill} будет забыт: следующее сообщение начнёт разговор с чистого листа.
                Вернуться к старой сессии можно из списка «Сессии».
                """;

        var yes = action == Compact ? "✅ Да, сжать" : "✅ Да, новая сессия";

        return (html, new Keyboard([[Button(yes, $"ctx:yes:{action}"), Button("↩️ Нет", "ctx:no")]]));
    }
}
