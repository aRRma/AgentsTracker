namespace AgentsTracker.Gateway.Infrastructure.Telegram.Dispatch;

/// <summary>Разобранная слэш-команда шлюза: кто прислал, что вызвал и с каким аргументом.</summary>
public sealed record TelegramCommandContext(long ChatId, long UserId, string Command, string Argument);
