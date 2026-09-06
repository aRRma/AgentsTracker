using System.Net;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>
/// Клиент Telegram Bot API через <see cref="IHttpClientFactory"/>: прокси из конфига,
/// стандартный конвейер устойчивости и ротация соединений, чтобы смена DNS у
/// api.telegram.org не требовала перезапуска шлюза.
/// </summary>
public static class TelegramClientFactory
{
    public const string HttpClientName = "telegram";

    /// <summary>
    /// Long polling getUpdates держит запрос до 100 с (<c>TelegramBotClient.Timeout</c>), и
    /// стандартные 10 с на попытку обрывали бы каждый опрос. Ограничения библиотеки:
    /// общий таймаут не меньше попытки, окно предохранителя не меньше двух попыток.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan BreakerSampling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Клиент живёт в синглтонах (<c>TelegramBotService</c>, статус запуска), поэтому
    /// ротация обработчика фабрикой до него не доходит — соединения пересоздаёт сам handler.
    /// </summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddTelegramBotClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName)
            // Логгер фабрики пишет URL каждого запроса, а у Bot API токен — часть пути.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new SocketsHttpHandler { PooledConnectionLifetime = ConnectionLifetime };
                if (sp.GetRequiredService<IOptions<GatewayOptions>>().Value.Proxy is { Length: > 0 } proxy)
                {
                    handler.Proxy = new WebProxy(proxy);
                    handler.UseProxy = true;
                }
                return handler;
            })
            .AddStandardResilienceHandler(options =>
            {
                options.AttemptTimeout.Timeout = AttemptTimeout;
                options.TotalRequestTimeout.Timeout = TotalTimeout;
                options.CircuitBreaker.SamplingDuration = BreakerSampling;
            })
            // BaseAddress не задаём: библиотека сама собирает полный URL на каждый вызов.
            .Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(
                sp.GetRequiredService<IOptions<GatewayOptions>>().Value.BotToken,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        return services;
    }
}
