using AgentsTracker.Agents.Claude;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Features.Audit;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Features.Help;
using AgentsTracker.Gateway.Features.Monitor;
using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Modules;
using AgentsTracker.Gateway.Infrastructure.Security;

// Служебная команда: зашифровать секреты локального конфига и перенести его в папку данных.
if (args is [ProtectSecretsCommand.Name, ..])
    return ProtectSecretsCommand.Run(args, Console.Out);

// Единственное место, где хост знает конкретных агентов. Новый агент — свой проект
// с IAgentBackendModule и строка здесь.
IReadOnlyList<IAgentBackendModule> agents = [new ClaudeAgentModule()];

// Порядок важен: текстовые обработчики опрашиваются в порядке регистрации, и ChatModule
// с его «поймать всё» должен идти последним.
IReadOnlyList<IFeatureModule> modules =
[
    new ApprovalsModule(),
    new SettingsModule(),
    new HelpModule(),
    new AuditModule(),
    new MonitorModule(),
    new ChatModule(),
];

// Content root — папка exe, а не текущая папка процесса: иначе запуск из другой папки
// (Start-Process из корня репозитория, ярлык) молча теряет appsettings.json.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.AddGatewayConfiguration();

// Бэкенд выбирается до сборки контейнера: он сам регистрирует свои сервисы.
var agentId = builder.Configuration[$"{GatewayOptions.SectionName}:Agent"] ?? new GatewayOptions().Agent;
var agent = agents.FirstOrDefault(a => a.Id.Equals(agentId, StringComparison.OrdinalIgnoreCase));
if (agent is null)
{
    Console.Error.WriteLine(
        $"{GatewayOptions.SectionName}:Agent = '{agentId}'. Известные агенты: {string.Join(", ", agents.Select(a => a.Id))}.");
    return 1;
}

builder.AddGatewayInfrastructure(agent, modules);

var app = builder.Build();

if (app.ValidateStartup() is not 0 and var exitCode) return exitCode;

app.MapFeatures(agent, modules);

await app.RunAsync();

// Ненулевой код ставит TelegramBotService, когда не смог подключиться: по нему Планировщик
// перезапускает задачу.
return Environment.ExitCode;
