namespace AgentsTracker.Gateway.Infrastructure.Chat.Dispatch;

/// <summary>Разобранная слэш-команда шлюза: кто прислал, что вызвал и с каким аргументом.</summary>
public sealed record ChatCommandContext(ChatId Chat, UserId User, string Command, string Argument);

/// <summary>
/// Обработчик слэш-команд шлюза. Команды, которых нет ни у одного обработчика, — это
/// команды самого агента (<c>/review</c> и прочие): они уходят ему как обычный текст.
/// </summary>
public interface IChatCommandHandler
{
    /// <summary>Команды в нижнем регистре, с ведущим «/», без «@bot» и без аргумента.</summary>
    IReadOnlyCollection<string> Commands { get; }

    Task HandleAsync(ChatCommandContext context, CancellationToken ct);
}
