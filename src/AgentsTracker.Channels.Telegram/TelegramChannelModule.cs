using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsTracker.Channels.Telegram;

/// <summary>Telegram как канал шлюза: бот с long polling, инлайн-кнопки, HTML-разметка.</summary>
public sealed class TelegramChannelModule : IChatChannelModule
{
    public string Id => TelegramChannel.ChannelId;

    /// <summary>Токен — очевидно; прокси — в его URI бывает user:pass.</summary>
    public IReadOnlyList<string> SecretKeys { get; } = [nameof(TelegramOptions.BotToken), nameof(TelegramOptions.Proxy)];

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TelegramOptions>(configuration.GetSection(ChannelConfiguration.SettingsSection));
        services.AddTelegramBotClient();
        services.AddSingleton<IChatChannel, TelegramChannel>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}
