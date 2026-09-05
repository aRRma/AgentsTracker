using System.Net;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Claude;
using AgentsTracker.Gateway.Infrastructure.Mcp;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Monitoring;
using AgentsTracker.Gateway.Infrastructure.Security;
using AgentsTracker.Gateway.Infrastructure.Telegram;
using Telegram.Bot;

namespace AgentsTracker.Gateway.Infrastructure;

/// <summary>Регистрация технической части — всего, что не принадлежит ни одной фиче.</summary>
public static class GatewayInfrastructure
{
    extension(WebApplicationBuilder builder)
    {
        /// <summary>
        /// Конфиг слоями: appsettings.json → appsettings.Local.json рядом с приложением (отладка
        /// из IDE) → appsettings.Local.json в папке данных (боевой, с DPAPI-секретами) → переменные
        /// окружения. Последний слой побеждает.
        /// </summary>
        public void AddGatewayConfiguration()
        {
            builder.Configuration.AddProtectedJsonFile("appsettings.Local.json", optional: true);
            builder.Configuration.AddProtectedJsonFile(AppPaths.LocalSettings, optional: true);
            builder.Configuration.AddEnvironmentVariables();
        }

        public void AddGatewayInfrastructure(IReadOnlyList<IFeatureModule> modules)
        {
            var services = builder.Services;
            var configuration = builder.Configuration;

            services.Configure<GatewayOptions>(configuration.GetSection(GatewayOptions.SectionName));

            services.AddSingleton<IAuditLog, JsonlAuditLog>();
            services.AddSingleton<SessionStore>();
            services.AddSingleton<ProjectCatalog>();
            services.AddSingleton<ClaudeCliLocator>();
            services.AddSingleton<McpConfigFile>();
            services.AddSingleton<ClaudeRunner>();
            services.AddSingleton<ClaudeLimits>();
            services.AddSingleton<SkillCatalog>();

            services.AddSingleton<RunMonitor>();
            services.AddSingleton<RingBufferLog>();
            services.AddSingleton<ILoggerProvider, RingBufferLoggerProvider>();

            services.AddSingleton<ITelegramBotClient>(sp =>
                TelegramClientFactory.Create(sp.GetRequiredService<IOptions<GatewayOptions>>().Value));
            services.AddSingleton<BotCommandsCatalog>();
            services.AddHostedService<TelegramBotService>();

            foreach (var module in modules) module.AddServices(services, configuration);

            // MCP-эндпоинт подтверждений и монитор доступны только с этой машины. Монитор — на
            // отдельном порту: у него нет токена, и его можно выключить, не трогая MCP.
            var defaults = new GatewayOptions();
            var port = configuration.GetValue<int?>($"{GatewayOptions.SectionName}:McpPort") ?? defaults.McpPort;
            var monitorPort = configuration.GetValue<int?>($"{GatewayOptions.SectionName}:MonitorPort") ?? defaults.MonitorPort;
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, port);
                if (monitorPort > 0 && monitorPort != port) kestrel.Listen(IPAddress.Loopback, monitorPort);
            });
        }
    }

    extension(WebApplication app)
    {
        /// <summary>
        /// Проверяет конфиг и наличие CLI до старта. Возвращает код выхода: ненулевой —
        /// запускаться нельзя, причина уже в логе.
        /// </summary>
        public int ValidateStartup()
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
            var options = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;

            var errors = options.Validate();
            if (errors.Count > 0)
            {
                foreach (var error in errors) logger.LogCritical("{Error}", error);
                return 1;
            }

            try
            {
                var locator = app.Services.GetRequiredService<ClaudeCliLocator>();
                locator.Resolve();

                // Контракт с CLI держится на его недокументированном поведении, а версия
                // меняется сама собой: без записи в логе непонятно, чей ответ разбирали.
                if (locator.TryGetVersion() is { Length: > 0 } version)
                {
                    logger.LogInformation("Версия CLI: {Version}", version);
                    app.Services.GetRequiredService<RunMonitor>().CliVersion = version;
                }
                else
                {
                    logger.LogWarning("Не удалось получить версию CLI");
                }
            }
            catch (InvalidOperationException ex)
            {
                logger.LogCritical("{Message}", ex.Message);
                return 1;
            }

            DataDirectoryAcl.Restrict(AppPaths.DataDirectory, logger);

            if (options.MonitorPort > 0)
                logger.LogInformation("Монитор: http://127.0.0.1:{Port}/", options.MonitorPort);

            // Файл с секретом MCP не должен пережить процесс.
            var mcp = app.Services.GetRequiredService<McpConfigFile>();
            app.Lifetime.ApplicationStopping.Register(mcp.Dispose);

            return 0;
        }

        public void MapFeatures(IReadOnlyList<IFeatureModule> modules)
        {
            foreach (var module in modules) module.MapEndpoints(app);
        }
    }
}
