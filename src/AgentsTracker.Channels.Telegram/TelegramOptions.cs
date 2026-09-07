namespace AgentsTracker.Channels.Telegram;

/// <summary>Настройки канала Telegram — секция <see cref="ChannelConfiguration.SettingsSection"/>.</summary>
public sealed class TelegramOptions
{
    /// <summary>Токен бота от @BotFather.</summary>
    public string BotToken { get; set; } = "";

    /// <summary>Telegram user id, которым разрешено управлять агентом. Пусто = запрещено всем.</summary>
    public long[] AllowedUserIds { get; set; } = [];

    /// <summary>
    /// HTTP-прокси для api.telegram.org, например "http://127.0.0.1:2080". null — общий прокси
    /// хоста (<c>Gateway:Proxy</c>), а если и его нет — напрямую.
    /// </summary>
    public string? Proxy { get; set; }

    public IReadOnlyList<string> Validate()
    {
        var section = ChannelConfiguration.SettingsSection;
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(BotToken))
            errors.Add($"{section}:BotToken не задан. Создайте бота у @BotFather и впишите токен в appsettings.Local.json.");

        if (AllowedUserIds.Length == 0)
            errors.Add($"{section}:AllowedUserIds пуст. Запустите шлюз, напишите боту — id появится в логе — и впишите его сюда.");

        // Значение в текст ошибки не подставляем: в URI прокси бывает user:pass, а ошибка идёт в лог.
        if (Proxy is { Length: > 0 } && !Uri.TryCreate(Proxy, UriKind.Absolute, out _))
            errors.Add($"{section}:Proxy — некорректный URI (ожидается вида http://host:port).");

        return errors;
    }
}
