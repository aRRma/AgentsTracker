namespace AgentsTracker.Channels;

/// <summary>
/// Куда канал отдаёт входящее. Реализует хост: проверка доступа, разбор команд и маршрутизация
/// по фичам — его дело, канал только переводит свои обновления в общие записи.
/// </summary>
public interface IChatInbound
{
    Task OnMessageAsync(IncomingMessage message, CancellationToken ct);

    Task OnButtonAsync(ButtonPress press, CancellationToken ct);
}
