namespace AgentsTracker.Gateway.Infrastructure.Cli;

/// <summary>
/// Короткая сводка о конфиге для вывода команды <c>install</c>: заполнен ли он и на каких
/// портах поднимется шлюз. Читается обычным JSON-провайдером, без расшифровки: значения
/// секретов не нужны, нужен лишь факт, что они заданы.
/// </summary>
public sealed record LocalConfig(bool Found, bool HasToken, int AllowedUsers, int McpPort, int MonitorPort)
{
    public string Summary => !Found
        ? $"не найден, положите appsettings.Local.json в {AppPaths.DataDirectory}"
        : (HasToken, AllowedUsers) switch
        {
            (false, _) => "найден, но BotToken пуст",
            (true, 0) => "найден, но AllowedUserIds пуст — бот никого не пустит",
            (true, var users) => $"найден, разрешённых пользователей: {users}",
        };

    public string MonitorDescription => MonitorPort == 0 ? "выключен" : MonitorPort.ToString();

    public static LocalConfig Read()
    {
        var defaults = new GatewayOptions();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(AppPaths.LocalSettings, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var section = configuration.GetSection(GatewayOptions.SectionName);

        // Имена ключей канала хосту вообще-то неизвестны; здесь это только сводка для человека,
        // и у единственного канала они такие. Второй канал — повод спросить у его модуля.
        var channel = configuration.GetSection(ChannelConfiguration.SettingsSection);

        return new LocalConfig(
            Found: File.Exists(AppPaths.LocalSettings),
            HasToken: !string.IsNullOrWhiteSpace(channel["BotToken"]),
            AllowedUsers: channel.GetSection("AllowedUserIds").GetChildren().Count(),
            McpPort: section.GetValue<int?>("McpPort") ?? defaults.McpPort,
            MonitorPort: section.GetValue<int?>("MonitorPort") ?? defaults.MonitorPort);
    }
}
