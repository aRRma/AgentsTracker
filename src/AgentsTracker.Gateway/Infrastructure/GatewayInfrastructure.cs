using System.Net;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Monitoring;
using AgentsTracker.Gateway.Infrastructure.Security;

namespace AgentsTracker.Gateway.Infrastructure;

/// <summary>Регистрация технической части — всего, что не принадлежит ни одной фиче.</summary>
public static class GatewayInfrastructure
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Конфиг слоями: appsettings.json → appsettings.Local.json рядом с exe (отладка) →
        /// тот же файл в папке данных (боевой, с DPAPI-секретами) → переменные окружения.
        /// Последний слой побеждает.
        /// </summary>
        public void AddGatewayConfiguration()
        {
            builder.Configuration.AddProtectedJsonFile("appsettings.Local.json", optional: true);
            builder.Configuration.AddProtectedJsonFile(AppPaths.LocalSettings, optional: true);
            builder.Configuration.AddEnvironmentVariables();
        }

        public void AddGatewayInfrastructure(
            IAgentBackendModule agent, IChatChannelModule channel, IReadOnlyList<IFeatureModule> modules)
        {
            var services = builder.Services;
            var configuration = builder.Configuration;

            services.Configure<GatewayOptions>(configuration.GetSection(GatewayOptions.SectionName));

            services.AddSingleton<IAuditLog, JsonlAuditLog>();
            services.AddSingleton<SessionStore>();
            services.AddSingleton<ProjectCatalog>();

            services.AddSingleton<RunMonitor>();
            services.AddSingleton<RingBufferLog>();
            services.AddSingleton<ILoggerProvider, RingBufferLoggerProvider>();

            services.AddSingleton<ChatDispatcher>();
            services.AddSingleton<StartupNotice>();
            services.AddHostedService<ChatGatewayService>();

            // Монитор на своём порту: у него нет токена, и выключать его надо отдельно
            // от подтверждений. Порт подтверждений всегда loopback — его зовёт дочерний
            // процесс агента, дальше машины ему незачем.
            var defaults = new GatewayOptions();
            var port = configuration.GetValue<int?>($"{GatewayOptions.SectionName}:McpPort") ?? defaults.McpPort;
            var monitorPort = configuration.GetValue<int?>($"{GatewayOptions.SectionName}:MonitorPort") ?? defaults.MonitorPort;
            var monitorBind = configuration.GetValue<string?>($"{GatewayOptions.SectionName}:MonitorBind") ?? defaults.MonitorBind;
            var proxy = configuration.GetValue<string?>($"{GatewayOptions.SectionName}:Proxy");
            var approvalMinutes = configuration.GetValue<int?>($"{GatewayOptions.SectionName}:ApprovalTimeoutMinutes")
                ?? defaults.ApprovalTimeoutMinutes;

            // any нужен в контейнере: порт на 127.0.0.1 внутри него не опубликовать никаким -p.
            var monitorAddress = monitorBind.Equals(GatewayOptions.MonitorBindAny, StringComparison.OrdinalIgnoreCase)
                ? IPAddress.Any
                : IPAddress.Loopback;

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, port);
                if (monitorPort > 0 && monitorPort != port) kestrel.Listen(monitorAddress, monitorPort);
            });

            // Бэкенду и каналу — только нужное от хоста, без GatewayOptions целиком.
            services.AddSingleton(new AgentHost(AppPaths.DataDirectory, port, proxy, TimeSpan.FromMinutes(approvalMinutes)));
            services.AddSingleton(new ChannelHost(proxy));
            agent.AddServices(services, configuration);
            channel.AddServices(services, configuration);

            foreach (var module in modules) module.AddServices(services, configuration);
        }
    }

    extension(WebApplication app)
    {
        /// <summary>
        /// Проверяет конфиг и наличие агента до старта. Возвращает код выхода: ненулевой —
        /// запускаться нельзя, причина уже в логе.
        /// </summary>
        public int ValidateStartup()
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
            var options = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
            var agent = app.Services.GetRequiredService<IAgentBackend>();
            var channel = app.Services.GetRequiredService<IChatChannel>();

            // Переехавшие ключи — раньше проверки канала: иначе канал видит пустой токен
            // и говорит «не задан», а человек смотрит на заполненный конфиг.
            var errors = MovedChannelKeys(app.Services.GetRequiredService<IConfiguration>());
            if (errors.Count == 0) errors = options.Validate();
            if (errors.Count == 0) errors = options.ValidateFor(agent.Capabilities);
            if (errors.Count == 0) errors = channel.Validate();
            if (errors.Count > 0)
            {
                foreach (var error in errors) logger.LogCritical("{Error}", error);
                return 1;
            }

            try
            {
                var probe = agent.Probe();

                // Контракт держится на недокументированном поведении CLI, а обновляется он
                // сам: без версии в логе непонятно, чей ответ разбирали.
                if (probe.Version is { Length: > 0 } version)
                {
                    logger.LogInformation("{Agent}: версия {Version}", agent.DisplayName, version);
                    app.Services.GetRequiredService<RunMonitor>().CliVersion = version;
                }
                else
                {
                    logger.LogWarning("{Agent}: не удалось получить версию", agent.DisplayName);
                }
            }
            catch (InvalidOperationException ex)
            {
                logger.LogCritical("{Message}", ex.Message);
                return 1;
            }

            // Конфиг прочитан, а окружение шлюза агент наследует целиком: переопределения
            // вроде «Gateway__Channel__Settings__BotToken» ему видеть незачем. Убираем здесь,
            // чтобы об этом не помнил каждый бэкенд.
            HideGatewaySettingsFromChildren();

            DataDirectoryAcl.Restrict(AppPaths.DataDirectory, logger);

            if (options.MonitorPort > 0) ReportMonitor(options, logger);

            return 0;
        }

        public void MapFeatures(
            IAgentBackendModule agent, IChatChannelModule channel, IReadOnlyList<IFeatureModule> modules)
        {
            agent.MapEndpoints(app);
            channel.MapEndpoints(app);
            foreach (var module in modules) module.MapEndpoints(app);
        }
    }

    /// <summary>
    /// Ключи канала переехали в его секцию, а старый конфиг выглядит рабочим: шлюз стартовал
    /// бы с пустым списком разрешённых и молчащим ботом. Лучше не стартовать и подсказать,
    /// куда переложить.
    /// </summary>
    private static IReadOnlyList<string> MovedChannelKeys(IConfiguration configuration)
    {
        return
        [
            .. ChannelOptions.MovedKeys
                .Where(key => configuration.GetSection($"{GatewayOptions.SectionName}:{key}").Exists())
                .Select(key => $"{GatewayOptions.SectionName}:{key} больше не читается — перенесите его "
                             + $"в {ChannelConfiguration.SettingsSection}:{key}."),
        ];
    }

    /// <summary>
    /// Где искать монитор и не открыт ли он лишним. Пароля у страницы нет, поэтому и
    /// <c>any</c> вне контейнера (монитор в сети), и <c>loopback</c> внутри (снаружи
    /// не достать) стоят предупреждения.
    /// </summary>
    private static void ReportMonitor(GatewayOptions options, ILogger logger)
    {
        var any = options.MonitorBind.Equals(GatewayOptions.MonitorBindAny, StringComparison.OrdinalIgnoreCase);

        logger.LogInformation("Монитор: http://{Host}:{Port}/", any ? "0.0.0.0" : "127.0.0.1", options.MonitorPort);

        if (any && !Container.Detected)
        {
            logger.LogWarning(
                "Gateway:MonitorBind = any вне контейнера: монитор без пароля доступен всем в сети. "
                + "Верните loopback, если это не то, что нужно.");
        }
        else if (!any && Container.Detected)
        {
            logger.LogWarning(
                "Gateway:MonitorBind = loopback в контейнере: снаружи монитор недоступен. "
                + "Поставьте any и публикуйте порт как {Publish}.",
                $"127.0.0.1:{options.MonitorPort}:{options.MonitorPort}");
        }
    }

    private static void HideGatewaySettingsFromChildren()
    {
        foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>().Where(IsGatewaySetting).ToArray())
            Environment.SetEnvironmentVariable(name, null);
    }

    /// <summary>
    /// Переменная, перекрывающая секцию Gateway, в любой из двух форм: на Windows
    /// AddEnvironmentVariables понимает и «Gateway__X», и «Gateway:X».
    /// </summary>
    private static bool IsGatewaySetting(string name) =>
        name.StartsWith($"{GatewayOptions.SectionName}__", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith($"{GatewayOptions.SectionName}:", StringComparison.OrdinalIgnoreCase);
}
