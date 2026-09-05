namespace AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

/// <summary>
/// Обработчик слэш-команд шлюза. Команды, которых нет ни у одного обработчика, — это
/// команды самого агента (<c>/review</c> и прочие): они уходят ему как обычный текст.
/// </summary>
public interface ITelegramCommandHandler
{
    /// <summary>Команды в нижнем регистре, с ведущим «/», без «@bot» и без аргумента.</summary>
    IReadOnlyCollection<string> Commands { get; }

    Task HandleAsync(TelegramCommandContext context, CancellationToken ct);
}
