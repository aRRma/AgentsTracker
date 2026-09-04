using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Features.Audit;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Features.Help;
using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Security;

// Служебная команда: зашифровать секреты локального конфига и перенести его в папку данных.
if (args is [ProtectSecretsCommand.Name, ..])
    return ProtectSecretsCommand.Run(args, Console.Out);

// Порядок важен: текстовые обработчики опрашиваются в порядке регистрации, и ChatModule
// с его «поймать всё» должен идти последним.
IReadOnlyList<IFeatureModule> modules =
[
    new ApprovalsModule(),
    new SettingsModule(),
    new HelpModule(),
    new AuditModule(),
    new ChatModule(),
];

var builder = WebApplication.CreateBuilder(args);
builder.AddGatewayConfiguration();
builder.AddGatewayInfrastructure(modules);

var app = builder.Build();

if (app.ValidateStartup() is not 0 and var exitCode) return exitCode;

app.MapFeatures(modules);

await app.RunAsync();

// Ненулевой код ставит TelegramBotService, когда не смог подключиться: по нему Планировщик
// перезапускает задачу.
return Environment.ExitCode;
