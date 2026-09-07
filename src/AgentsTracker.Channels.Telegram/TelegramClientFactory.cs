using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace AgentsTracker.Channels.Telegram;

/// <summary>
/// Клиент Bot API через <see cref="IHttpClientFactory"/>: прокси из конфига, конвейер
/// устойчивости и ротация соединений — смена DNS у api.telegram.org не должна требовать
/// перезапуска шлюза.
/// </summary>
internal static class TelegramClientFactory
{
    public const string HttpClientName = "telegram";

    /// <summary>
    /// getUpdates держит запрос до 100 с, и стандартные 10 с на попытку обрывали бы каждый
    /// опрос. Ограничения библиотеки: общий таймаут не меньше попытки, окно предохранителя
    /// не меньше двух попыток.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan BreakerSampling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Клиент живёт в синглтоне канала, и ротация обработчика фабрикой до него не доходит —
    /// соединения пересоздаёт сам handler. Срок тот же, что у клиентов фабрики.
    /// </summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(3);

    public static IServiceCollection AddTelegramBotClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName)
            // Логгер фабрики пишет URL каждого запроса, а токен Bot API — часть пути.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new SocketsHttpHandler { PooledConnectionLifetime = ConnectionLifetime };

                // Свой прокси канала важнее общего. Пустая строка своим не считается:
                // шаблон конфига оставляет "" вместо null, и канал пошёл бы напрямую.
                var proxy = sp.GetRequiredService<IOptions<TelegramOptions>>().Value.Proxy is { Length: > 0 } own
                    ? own
                    : sp.GetRequiredService<ChannelHost>().Proxy;
                if (proxy is { Length: > 0 })
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

                // Повторяем только то, что до Telegram не дошло: методы Bot API — POST без
                // идемпотентности, и повтор после 5xx на принятом sendMessage даст дубль
                // в чате. 429 тоже не повторяем — по retry_after ждёт вызывающий.
                options.Retry.ShouldHandle = args =>
                    ValueTask.FromResult(args.Outcome.Exception is HttpRequestException);
            })
            // BaseAddress не задаём: библиотека сама собирает полный URL на каждый вызов.
            .Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(
                sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        // Канал берёт клиент лениво: конструктор TelegramBotClient бросает ArgumentException
        // на пустом токене, а канал создаётся раньше проверки настроек — вместо понятного
        // «BotToken не задан» вышел бы стектрейс.
        services.AddSingleton(sp => new Lazy<ITelegramBotClient>(sp.GetRequiredService<ITelegramBotClient>));

        return services;
    }
}
