using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Сессии текущего проекта: какая активна, переключение, новая, остановка идущего запуска,
/// очистка списка. «Остановить» здесь же: запуск и сессия — одна рабочая единица,
/// и прерывать его удобнее там, где видно, в какой ветке он идёт.
/// </summary>
public sealed class SessionsScreen(SessionStore store, IAgentBackend agent, ChatWorker worker, IAuditLog audit) : ISettingsScreen
{
    private const int MaxShown = 8;

    public string Key => "sess";

    public string? Apply(string argument, long userId, long chatId)
    {
        var project = store.ProjectPath;
        var previous = store.SessionId;

        if (argument == "new")
        {
            store.SetSessionId(null);
            audit.Changed(store, userId, "session", previous, "новая");
            return "Следующее сообщение начнёт новую сессию";
        }

        if (argument == "stop")
        {
            var stopped = worker.Stop();
            audit.Write(AuditEvent.Now(AuditKinds.Gateway, "/stop", userId, chatId, project, previous,
                stopped ? "stopped" : "idle"));
            return stopped ? "Остановлено" : "Сейчас ничего не выполняется";
        }

        if (argument == "clear")
        {
            var removed = store.ForgetSessions(project);
            if (removed > 0) audit.Changed(store, userId, "sessions", $"{removed}", "очищено");
            return removed == 0 ? "Список и так пуст" : $"Забыто сессий: {removed}";
        }

        // По началу id, а не по номеру: список упорядочен по активности, и завершившийся
        // между отрисовкой и нажатием запуск сдвинул бы номера на соседнюю сессию.
        var session = store.SessionsFor(project).FirstOrDefault(s => s.Id.ShortId == argument);
        if (session is null) return "Сессии уже нет в списке";

        store.SetSessionId(session.Id);
        audit.Changed(store, userId, "session", previous?.ShortId, session.Id.ShortId);
        return worker.IsBusy ? "Сессия сменится со следующего запуска" : "Сессия выбрана";
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(long userId, CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        var project = store.ProjectPath;
        var active = store.SessionId;
        var sessions = store.SessionsFor(project).Take(MaxShown).ToArray();
        var current = sessions.FirstOrDefault(s => s.Id == active);
        var busy = worker.IsBusy;

        var now = busy
            ? $"🟢 <b>Выполняется</b>{(current is null ? "" : $" в «{E(current.Title)}»")}, в очереди: {worker.QueueLength}"
            : current is null
                ? "⚪ Активной сессии нет — следующее сообщение начнёт новую"
                : $"▶ Активна: <b>{E(current.Title)}</b>";

        var body = sessions.Length == 0
            ? "<i>Сессий ещё нет — первое сообщение создаст первую.</i>"
            : string.Join("\n", sessions.Select((s, i) =>
                $"{Marker(s.Id == active)} <b>{i + 1}.</b> {E(s.Title)}\n" +
                $"   {s.Turns} х · {E(s.LastActivityUtc.Ago)}"));

        var html = $"""
            🧵 <b>Сессии</b> — {E(Path.GetFileName(project))}

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
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }
}
