using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;

namespace AgentsTracker.Channels.Telegram;

/// <summary>
/// Клиент Telegram Bot API через <see cref="IHttpClientFactory"/>: прокси из конфига,
/// конвейер устойчивости и ротация соединений, чтобы смена DNS у api.telegram.org
/// не требовала перезапуска шлюза.
/// </summary>
internal static class TelegramClientFactory
{
    public const string HttpClientName = "telegram";

    /// <summary>
    /// Long polling getUpdates держит запрос до 100 с (<c>TelegramBotClient.Timeout</c>, он же
    /// <c>HttpClient.Timeout</c>), и стандартные 10 с на попытку обрывали бы каждый опрос.
    /// Ограничения библиотеки: общий таймаут не меньше попытки, окно предохранителя не меньше
    /// двух попыток.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan BreakerSampling = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Клиент живёт в синглтоне канала, поэтому ротация обработчика фабрикой до него не
    /// доходит — соединения пересоздаёт сам handler. Срок тот же, что у клиента, который
    /// Telegram.Bot собирает сам.
    /// </summary>
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(3);

    public static IServiceCollection AddTelegramBotClient(this IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName)
            // Логгер фабрики пишет URL каждого запроса, а у Bot API токен — часть пути.
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var handler = new SocketsHttpHandler { PooledConnectionLifetime = ConnectionLifetime };

                // Свой прокси канала важнее общего прокси хоста. Пустая строка — не «свой»:
                // шаблон конфига оставляет "" вместо null, и канал молча пошёл бы напрямую.
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

                // Повторяем только то, что до Telegram не дошло. Все методы Bot API — POST без
                // идемпотентности: повтор после 5xx на уже принятом sendMessage — дубль в чате.
                // 429 не повторяем: retry_after лежит в теле ответа, библиотека отдаёт его как
                // ApiRequestException.Parameters.RetryAfter, и ждать по нему должен вызывающий.
                options.Retry.ShouldHandle = args =>
                    ValueTask.FromResult(args.Outcome.Exception is HttpRequestException);
            })
            // BaseAddress не задаём: библиотека сама собирает полный URL на каждый вызов.
            .Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(
                sp.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName)));

        // Клиент канал берёт лениво: конструктор TelegramBotClient сам проверяет токен и на
        // пустом или неверном бросает ArgumentException, а канал создаётся раньше проверки
        // настроек — иначе вместо понятного «BotToken не задан» пользователь видел бы стектрейс.
        services.AddSingleton(sp => new Lazy<ITelegramBotClient>(sp.GetRequiredService<ITelegramBotClient>));

        return services;
    }
}
