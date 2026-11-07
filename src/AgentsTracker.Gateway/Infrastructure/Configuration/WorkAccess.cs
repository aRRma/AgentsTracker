namespace AgentsTracker.Gateway.Infrastructure.Configuration;

/// <summary>
/// Кому доступна тема «Работа». Id из конфига сверяются с <see cref="UserId.Key"/>: разбирать id
/// конкретного канала хост не умеет, а строку «канал:значение» отдаёт любой адрес пользователя.
/// </summary>
public sealed class WorkAccess(IOptions<GatewayOptions> options, IChatChannel channel, IAgentBackend agent)
{
    private readonly HashSet<string> _keys = new(
        options.Value.Work.AllowedUserIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => KeyOf(id.Trim(), channel.Id)),
        StringComparer.Ordinal);

    /// <summary>
    /// Тема «Работа» есть хоть у кого-то: без этого кнопки «Работа» нет ни у кого. Агенту без
    /// режима «спрашивать всё» её не дать — закрепить было бы нечего.
    /// </summary>
    public bool Enabled => _keys.Count > 0 && agent.Capabilities.AskingPermissionMode is not null;

    /// <summary>Режим, которым закреплена тема «Работа». Есть, только если <see cref="Enabled"/>.</summary>
    public string PermissionMode => agent.Capabilities.AskingPermissionMode
        ?? throw new InvalidOperationException("У агента нет режима, которым можно закрепить тему «Работа».");

    public bool Allows(UserId user) => Enabled && _keys.Contains(user.Key);

    /// <summary>
    /// Id не из списка пользователей канала — опечатка: такой человек боту не напишет, а владелец
    /// думал бы, что выдал доступ. Агент без закреплённого режима — тоже ошибка конфига, а не
    /// молча пропавшая кнопка.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var key = $"{GatewayOptions.SectionName}:Work:AllowedUserIds";

        if (_keys.Count > 0 && agent.Capabilities.AskingPermissionMode is null)
            errors.Add($"{key} задан, а у агента {agent.DisplayName} нет режима, которым закрепляется тема «Работа».");

        var known = channel.AllowedUsers.Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
        errors.AddRange(_keys.Where(id => !known.Contains(id))
            .Select(id => $"{key} — {id} нет среди пользователей канала (Channel:Settings:AllowedUserIds)."));

        return errors;
    }

    private static string KeyOf(string id, string channelId) => id.Contains(':') ? id : $"{channelId}:{id}";
}
