using AgentsTracker.Gateway.Infrastructure.Audit;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>
/// Сообщение в чат при старте шлюза: «запущен» всем из AllowedUserIds, а тому, чей запуск
/// прошлый экземпляр не довёл до конца, — ещё и «прерван». Без этого перезапуск посреди
/// работы (Stop-Process из другой сессии, падение) выглядит как молчание: статус «Работаю…»
/// висит, ответа нет, и человек узнаёт о проблеме, только переспросив.
/// </summary>
public sealed class StartupNotice(
    ITelegramBotClient bot,
    SessionStore store,
    IAgentBackend agent,
    IAuditLog audit,
    IOptions<GatewayOptions> options,
    ILogger<StartupNotice> logger)
{
    public async Task SendAsync(CancellationToken ct)
    {
        var interrupted = store.TakeInterruptedRun();
        if (interrupted is not null) RecordInterrupted(interrupted);

        var probe = agent.Probe();
        var version = probe.Version is { Length: > 0 } v ? $" {v}" : "";
        var started = $"🔌 Шлюз запущен — {agent.DisplayName}{version}, проект {Path.GetFileName(store.ProjectPath)}.";

        // В личном чате id чата равен id пользователя, поэтому список получателей — AllowedUserIds.
        foreach (var userId in options.Value.AllowedUserIds.Distinct())
        {
            var text = interrupted is { } run && run.ChatId == userId
                ? started + "\n\n" + Interrupted(run)
                : started;

            await SendQuietlyAsync(userId, text, ct);
        }

        // Прерванный запуск шёл в чате, которого в AllowedUserIds уже нет (список правили) —
        // сказать всё равно надо, ответ там так и не пришёл.
        if (interrupted is { } orphan && !options.Value.AllowedUserIds.Contains(orphan.ChatId))
            await SendQuietlyAsync(orphan.ChatId, started + "\n\n" + Interrupted(orphan), ct);
    }

    private static string Interrupted(ActiveRun run)
    {
        var elapsed = (DateTimeOffset.UtcNow - run.StartedUtc).Elapsed;
        var session = run.SessionId is { Length: > 0 } id ? id.ShortId : "новая";

        return $"""
            ⚠️ Предыдущий запуск прерван перезапуском шлюза — ответа по нему не будет.
            «{run.Prompt}»
            Начат {run.StartedUtc.Ago}, прошло {elapsed}; сессия {session}.
            Сделанное до обрыва в сессии сохранено — напишите «продолжай», чтобы агент довёл дело до конца.
            """;
    }

    /// <summary>Итог в историю и аудит: иначе в мониторе запуск просто исчезает, а в аудите остаётся run.start без run.end.</summary>
    private void RecordInterrupted(ActiveRun run)
    {
        logger.LogWarning("Прошлый экземпляр шлюза умер посреди запуска «{Prompt}» (начат {StartedUtc})", run.Prompt, run.StartedUtc);

        audit.Write(AuditEvent.Now(AuditKinds.RunEnd, "прерван перезапуском шлюза",
            run.UserId, run.ChatId, run.ProjectPath, run.SessionId, "interrupted"));

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

    private async Task SendQuietlyAsync(long chatId, string text, CancellationToken ct)
    {
        try
        {
            await bot.SendMessage(chatId, text, cancellationToken: ct);
        }
        catch (ApiRequestException ex)
        {
            // Пользователь из списка ещё не писал боту: Telegram не даёт боту начать разговор первым.
            logger.LogDebug(ex, "Сообщение о старте не доставлено в чат {ChatId}", chatId);
        }
    }
}
