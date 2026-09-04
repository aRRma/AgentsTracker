using System.Net;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>Клиент Telegram Bot API с учётом прокси из конфига.</summary>
public static class TelegramClientFactory
{
    public static ITelegramBotClient Create(GatewayOptions options)
    {
        if (options.Proxy is not { Length: > 0 } proxy)
            return new TelegramBotClient(options.BotToken);

        var handler = new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true };
        return new TelegramBotClient(options.BotToken, new HttpClient(handler));
    }
}
