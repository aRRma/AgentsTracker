using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Infrastructure.Chat;

/// <summary>
/// Жизнь связи с чатом: дождаться канала, опубликовать команды, сказать «запущен» и слушать
/// входящее до остановки. Что именно делать с сообщением, решает <see cref="ChatDispatcher"/>.
/// </summary>
public sealed class ChatGatewayService(
    IChatChannel channel,
    ChatDispatcher dispatcher,
    StartupNotice notice,
    SessionStore store,
    IAuditLog audit,
    IHostApplicationLifetime lifetime,
    ILogger<ChatGatewayService> logger) : BackgroundService
{
    /// <summary>
    /// Сколько раз пробовать, если канал не принимает токен: это не проходит само, но пробный
    /// экземпляр с выдуманным токеном как раз и живёт эти попытки, а потом выходит.
    /// </summary>
    private const int UnauthorizedAttempts = 6;

    /// <summary>
    /// Пауза между попытками растёт от первой до потолка. Сеть, которой нет, ждём без предела:
    /// компьютер включается раньше роутера, а шлюз, который сдался через пару минут, Планировщик
    /// перезапускает не больше трёх раз с интервалом в минуту — дальше он молчал до следующего
    /// входа в Windows.
    /// </summary>
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectRetryCeiling = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await ConnectAsync(stoppingToken))
        {
            // Без чата жить нельзя: хост выглядел бы работающим, а чат — мёртвым.
            // Ненулевой код выхода — повод для Планировщика перезапустить задачу.
            Environment.ExitCode = 1;
            lifetime.StopApplication();
            return;
        }

        audit.Write(AuditEvent.Now(AuditKinds.Gateway, "старт", project: store.ProjectPath));
        await notice.SendAsync(stoppingToken);

        try
        {
            await channel.ListenAsync(dispatcher, stoppingToken);
        }
        finally
        {
            audit.Write(AuditEvent.Now(AuditKinds.Gateway, "стоп"));
        }
    }

    private async Task<bool> ConnectAsync(CancellationToken stoppingToken)
    {
        var unauthorized = 0;
        var delay = ConnectRetryDelay;

        for (var attempt = 1; ; attempt++)
        {
            var refused = false;
            try
            {
                var bot = await channel.ConnectAsync(stoppingToken);
                logger.LogInformation("{Channel}: бот {Bot} готов. Проект: {Project}", channel.DisplayName, bot, store.ProjectPath);
                await channel.PublishCommandsAsync(ChatCommandCatalog.Commands, stoppingToken);
                return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return false;
            }
            catch (ChannelRequestException ex) when (ex.Kind == ChannelFailure.Unauthorized)
            {
                refused = true;
                if (++unauthorized >= UnauthorizedAttempts)
                {
                    logger.LogCritical(ex,
                        "{Channel} не принимает токен бота ({Attempts} попыток). Проверьте токен в настройках канала.",
                        channel.DisplayName, unauthorized);
                    return false;
                }

                logger.LogWarning("{Channel} не принял токен ({Message}), попытка {Attempt} из {Attempts}",
                    channel.DisplayName, ex.Message, unauthorized, UnauthorizedAttempts);
            }
            catch (Exception ex)
            {
                logger.LogWarning("{Channel} недоступен ({Message}), попытка {Attempt}, следующая через {Delay} с. " +
                                  "Проверьте сеть и прокси (Gateway:Proxy)",
                    channel.DisplayName, ex.Message, attempt, delay.TotalSeconds);
            }

            // Отказ в токене ждёт ровно паузу: растущая растянула бы минуту попыток на полчаса.
            try { await Task.Delay(refused ? ConnectRetryDelay : delay, stoppingToken); }
            catch (OperationCanceledException) { return false; }

            if (!refused) delay = delay * 2 < ConnectRetryCeiling ? delay * 2 : ConnectRetryCeiling;
        }
    }
}
