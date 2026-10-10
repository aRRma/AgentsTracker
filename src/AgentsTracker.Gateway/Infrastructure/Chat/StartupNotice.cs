using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Infrastructure.Chat;

/// <summary>
/// Сообщение при старте: «запущен» всем разрешённым, а тому, чей запуск прошлый экземпляр
/// не довёл до конца, — ещё и «прервано». Без этого перезапуск посреди работы выглядит
/// молчанием: статус висит, ответа нет, и человек узнаёт о проблеме, только переспросив.
/// </summary>
public sealed class StartupNotice(
    IChatChannel channel,
    SessionStore store,
    IAgentBackend agent,
    IAuditLog audit,
    ILogger<StartupNotice> logger)
{
    public async Task SendAsync(CancellationToken ct)
    {
        var interrupted = store.TakeInterruptedRuns();
        foreach (var run in interrupted) RecordInterrupted(run);

        var probe = agent.Probe();
        var version = probe.Version is { Length: > 0 } v ? $" {v}" : "";

        // Версия шлюза рядом с версией агента: после «распакуйте архив поверх» это
        // единственный способ из чата убедиться, что поднялась новая сборка.
        var started = $"🔌 Шлюз запущен {AppVersion.Current} — {agent.DisplayName}{version}, "
                      + $"проект {Path.GetFileName(store.ProjectPath)}.";

        // Потерю состояния видно сразу, а не когда не найдётся сессия или карточка доверия
        // придёт к проекту, которому уже доверяли.
        if (store.LoadProblem is { } problem) started += "\n\n" + problem;

        // Задача и вопрос могли оборваться оба, и в одном чате: оба «прервано» — одним сообщением.
        var parsed = interrupted
            .Select(run => (Run: run, Chat: channel.ParseChat(run.ChatKey), Text: Interrupted(run)))
            .ToList();

        // Чат не этого канала (сменили канал в конфиге) — сказать некуда, но молча терять
        // «прервано» нельзя: в журнал и историю он уже записан, пусть будет и в логе.
        foreach (var lost in parsed.Where(notice => notice.Chat is null))
            logger.LogWarning("«Прервано» не доставить: чат {ChatKey} не разобран каналом", lost.Run.ChatKey);

        var notices = parsed
            .Where(notice => notice.Chat is not null)
            .GroupBy(notice => notice.Chat!, notice => notice.Text)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var chat in channel.AllowedUsers.Select(channel.DirectChat).OfType<ChatId>())
        {
            var mine = notices.Remove(chat, out var texts) ? texts : [];
            await SendQuietlyAsync(chat, string.Join("\n\n", [started, .. mine]), ct);
        }

        // Прерванный запуск шёл в чате, которого в списке разрешённых уже нет или который
        // канал не смог вычислить. Сказать всё равно надо: ответа там так и не было.
        foreach (var (chat, texts) in notices)
            await SendQuietlyAsync(chat, string.Join("\n\n", [started, .. texts]), ct);
    }

    private static string Interrupted(ActiveRun run)
    {
        var elapsed = (DateTimeOffset.UtcNow - run.StartedUtc).Elapsed;

        if (run.Question)
        {
            return $"""
                ⚠️ Вопрос прерван перезапуском шлюза — ответа на него не будет.
                «{run.Prompt}»
                Задан {run.StartedUtc.Ago}, прошло {elapsed}. Задайте его снова через /ask.
                """;
        }

        var session = run.SessionId is { Length: > 0 } id ? id.ShortId : "новая";

        return $"""
            ⚠️ Предыдущий запуск прерван перезапуском шлюза — ответа по нему не будет.
            «{run.Prompt}»
            Начат {run.StartedUtc.Ago}, прошло {elapsed}; сессия {session}.
            Сделанное до обрыва в сессии сохранено — напишите «продолжай», чтобы агент довёл дело до конца.
            """;
    }

    /// <summary>Итог в историю и аудит: иначе в мониторе запуск исчезает, а в аудите остаётся run.start без run.end.</summary>
    private void RecordInterrupted(ActiveRun run)
    {
        logger.LogWarning("Прошлый экземпляр шлюза умер посреди запуска «{Prompt}» (начат {StartedUtc})", run.Prompt, run.StartedUtc);

        audit.Write(AuditEvent.NowByKeys(AuditKinds.RunEnd, "прерван перезапуском шлюза",
            run.UserKey, run.ChatKey, run.ProjectPath, run.SessionId, "interrupted"));

        store.RecordRunOutcome(new RunRecord
        {
            StartedUtc = run.StartedUtc,
            ProjectPath = run.ProjectPath,
            SessionId = run.SessionId,
            Prompt = run.Prompt,
            Model = run.Model,
            Outcome = "interrupted",
            DurationMs = (long)(DateTimeOffset.UtcNow - run.StartedUtc).TotalMilliseconds,
        });
    }

    private async Task SendQuietlyAsync(ChatId chat, string text, CancellationToken ct)
    {
        try
        {
            await channel.SendAsync(chat, new OutgoingMessage(text, Rich: false), ct);
        }
        catch (ChannelRequestException ex)
        {
            // Пользователь из списка ещё не писал боту — начать разговор первым канал не даёт.
            logger.LogDebug(ex, "Сообщение о старте не доставлено в чат {Chat}", chat.Key);
        }
    }
}
