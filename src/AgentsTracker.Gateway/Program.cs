using System.Net;
using AgentsTracker.Gateway.Approvals;
using AgentsTracker.Gateway.Claude;
using AgentsTracker.Gateway.Configuration;
using AgentsTracker.Gateway.State;
using AgentsTracker.Gateway.Telegram;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.SectionName));

var options = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();

// MCP-эндпоинт подтверждений доступен только с этой машины.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.McpPort));

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ProjectCatalog>();
builder.Services.AddSingleton<ClaudeCliLocator>();
builder.Services.AddSingleton<McpConfigFile>();
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddSingleton<ClaudeLimits>();
builder.Services.AddSingleton<ApprovalBroker>();

builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var gateway = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;

    if (gateway.Proxy is not { Length: > 0 } proxy)
        return new TelegramBotClient(gateway.BotToken);

    var handler = new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true };
    return new TelegramBotClient(gateway.BotToken, new HttpClient(handler));
});

builder.Services.AddSingleton<ChatWorker>();
builder.Services.AddSingleton<SettingsMenu>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatWorker>());
builder.Services.AddHostedService<TelegramBotService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<PermissionTool>();

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

var errors = options.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors) startupLogger.LogCritical("{Error}", error);
    return 1;
}

try
{
    app.Services.GetRequiredService<ClaudeCliLocator>().Resolve();
}
catch (InvalidOperationException ex)
{
    startupLogger.LogCritical("{Message}", ex.Message);
    return 1;
}

app.MapMcp(app.Services.GetRequiredService<McpConfigFile>().RoutePattern);

await app.RunAsync();
return 0;
