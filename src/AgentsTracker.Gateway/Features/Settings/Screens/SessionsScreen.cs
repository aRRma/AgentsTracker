using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Сессии текущего проекта: какая активна, переключение, новая, остановка идущего запуска,
/// очистка списка. «Остановить» здесь же: запуск и сессия — одна рабочая единица,
/// и прерывать его удобнее там, где видно, в какой ветке он идёт.
/// </summary>
public sealed class SessionsScreen(
    SessionStore store, IAgentBackend agent, ChatWorker worker, AttachmentInbox inbox, IAuditLog audit) : ISettingsScreen
{
    private const int MaxShown = 8;

    public string Key => "sess";

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        var scope = store.For(chat);
        var project = scope.ProjectPath;
        var previous = scope.SessionId;

        if (argument == "new")
        {
            scope.SetSessionId(null);

            // Как и /new: прошлый контекст забыт, картинки к нему больше не нужны.
            inbox.ClearSession(chat, project);

            audit.Changed(scope, user, "session", previous, "новая");
            return "Следующее сообщение начнёт новую сессию";
        }

        if (argument == "stop")
        {
            var stopped = worker.Stop(chat);
            audit.Write(AuditEvent.Now(AuditKinds.Gateway, "/stop", user, chat, project, previous,
                stopped ? "stopped" : "idle"));
            return stopped ? "Остановлено" : "Сейчас ничего не выполняется";
        }

        if (argument == "clear")
        {
            var removed = scope.ForgetSessions();
            if (removed > 0) audit.Changed(scope, user, "sessions", $"{removed}", "очищено");
            return removed == 0 ? "Список и так пуст" : $"Забыто сессий: {removed}";
        }

        // По началу id, а не по номеру: список упорядочен по активности, и завершившийся
        // между отрисовкой и нажатием запуск сдвинул бы номера на соседнюю сессию.
        var session = store.SessionsFor(project).FirstOrDefault(s => s.Id.ShortId == argument);
        if (session is null) return "Сессии уже нет в списке";
        if (session.Id != previous && scope.IsActiveElsewhere(session.Id)) return "Сессия активна в другой теме или ленте — выберите другую или «Новая»";

        scope.SetSessionId(session.Id);
        audit.Changed(scope, user, "session", previous?.ShortId, session.Id.ShortId);
        return worker.IsBusy(chat) ? "Сессия сменится со следующего запуска" : "Сессия выбрана";
    }

    public Task<(string Html, Keyboard Keyboard)> RenderAsync(ChatId chat, CancellationToken ct) =>
        Task.FromResult(Render(chat));

    private (string Html, Keyboard Keyboard) Render(ChatId chat)
    {
        var scope = store.For(chat);
        var project = scope.ProjectPath;
        var active = scope.SessionId;
        var sessions = store.SessionsFor(project).Take(MaxShown).ToArray();
        var current = sessions.FirstOrDefault(s => s.Id == active);
        var busy = worker.IsBusy(chat);

        var now = busy
            ? $"🟢 <b>Выполняется</b>{(current is null ? "" : $" в «{E(current.Title)}»")}, в очереди: {worker.QueueLength(chat)}"
            : current is null
                ? "⚪ Активной сессии нет — следующее сообщение начнёт новую"
                : $"▶ Активна: <b>{E(current.Title)}</b>";

        var body = sessions.Length == 0
            ? "<i>Сессий ещё нет — первое сообщение создаст первую.</i>"
            : string.Join("\n", sessions.Select((s, i) =>
                $"{Marker(s.Id == active)} <b>{i + 1}.</b> {E(s.Title)}\n" +
                $"   {s.Turns} х · {E(s.LastActivityUtc.Ago)}{Context(s)}" +
                (s.Id != active && scope.IsActiveElsewhere(s.Id) ? " · 🔒 в другой теме или ленте" : "")));

        var html = $"""
            🧵 <b>Сессии</b> — {E(scope.IsTalk ? "беседа" : Path.GetFileName(project))}

            {now}

            {body}

            <i>Выбранная сессия продолжается со следующего сообщения.
            «Очистить» убирает записи только из списка шлюза — сами сессии остаются у {E(agent.DisplayName)}.</i>
            """;

        var buttons = sessions
            .Select((s, i) => Button($"{(s.Id == active ? "▶ " : "")}{i + 1}", $"sess:{s.Id.ShortId}"))
            .Chunk(4)
            .ToList();

        buttons.Add(busy
            ? [Button("🆕 Новая", "sess:new"), Button("🛑 Остановить", "sess:stop")]
            : [Button("🆕 Новая", "sess:new"), Button("🗑 Очистить", "sess:clear")]);

        // Вопрос — соседняя по смыслу кнопка: спросить, не трогая выбранную ветку. Экран «Вопрос»
        // отменяет ожидание сам, поэтому переход отсюда ничего не ломает.
        buttons.Add([Button("💬 Вопрос вне сессии", "ask")]);
        buttons.Add([BackButton]);

        return (html, new Keyboard(buttons));
    }

    // Заполненность рядом с каждой сессией: выбирая, к какой вернуться, видно, какую пора сжать.
    private static string Context(SessionRecord session) =>
        ContextScreen.Short(session) is { Length: > 0 } fill ? $" · 📦 {fill}" : "";
}
