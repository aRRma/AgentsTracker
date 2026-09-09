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
        var interrupted = store.TakeInterruptedRun();
        if (interrupted is not null) RecordInterrupted(interrupted);

        var probe = agent.Probe();
        var version = probe.Version is { Length: > 0 } v ? $" {v}" : "";

        // Версия шлюза рядом с версией агента: после «распакуйте архив поверх» это
        // единственный способ из чата убедиться, что поднялась новая сборка.
        var started = $"🔌 Шлюз запущен {AppVersion.Current} — {agent.DisplayName}{version}, "
                      + $"проект {Path.GetFileName(store.ProjectPath)}.";

        var interruptedChat = interrupted is null ? null : channel.ParseChat(interrupted.ChatKey);
        var told = false;

        foreach (var chat in channel.AllowedUsers.Select(channel.DirectChat).OfType<ChatId>())
        {
            var mine = interruptedChat is not null && chat == interruptedChat;
            told |= mine;

            await SendQuietlyAsync(chat, mine ? started + "\n\n" + Interrupted(interrupted!) : started, ct);
        }

        // Прерванный запуск шёл в чате, которого в списке разрешённых уже нет или который
        // канал не смог вычислить. Сказать всё равно надо: ответа там так и не было.
        if (!told && interruptedChat is not null)
            await SendQuietlyAsync(interruptedChat, started + "\n\n" + Interrupted(interrupted!), ct);
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
