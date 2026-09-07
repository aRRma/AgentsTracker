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
    /// Сколько раз пробовать достучаться до канала при старте: при автозапуске на вход
    /// в систему сеть часто поднимается позже шлюза.
    /// </summary>
    private const int ConnectAttempts = 6;
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(10);

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
        for (var attempt = 1; ; attempt++)
        {
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
            catch (Exception ex)
            {
                if (attempt >= ConnectAttempts)
                {
                    logger.LogCritical(ex,
                        "Не удалось подключиться к {Channel} за {Attempts} попыток. " +
                        "Проверьте токен и доступность сети (Gateway:Proxy).", channel.DisplayName, attempt);
                    return false;
                }

                logger.LogWarning("{Channel} недоступен ({Message}), попытка {Attempt} из {Attempts} через {Delay} с",
                    channel.DisplayName, ex.Message, attempt, ConnectAttempts, ConnectRetryDelay.TotalSeconds);
            }

            try { await Task.Delay(ConnectRetryDelay, stoppingToken); }
            catch (OperationCanceledException) { return false; }
        }
    }
}
