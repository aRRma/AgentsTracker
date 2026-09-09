namespace AgentsTracker.Channels;

/// <summary>
/// Один повтор после 429 с коротким retry_after — общее правило для всего, что шлёт в чат.
/// Ждать дольше потолка нельзя: за отправкой ждёт человек или инструмент агента, а не
/// бесконечная пауза. Без общего места у каждой отправки был бы свой потолок и своя
/// логика повтора, и правка одной терялась бы в остальных.
/// </summary>
public static class RateLimitRetry
{
    public static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Выполняет attempt; если канал ответил RateLimited с retry_after не больше ceiling —
    /// зовёт onWait, ждёт и повторяет attempt ещё раз. Второй отказ уходит вызывающему.
    /// </summary>
    public static async Task OnceAsync(Func<Task> attempt, Action<TimeSpan> onWait, CancellationToken ct, TimeSpan? ceiling = null)
    {
        try
        {
            await attempt();
            return;
        }
        catch (ChannelRequestException ex) when (ex is { Kind: ChannelFailure.RateLimited, RetryAfter: { } wait }
                                                 && wait <= (ceiling ?? DefaultCeiling))
        {
            onWait(wait);
            await Task.Delay(wait, ct);
        }

        await attempt();
    }
}
