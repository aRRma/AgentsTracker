using AgentsTracker.Agents.Claude;
using AgentsTracker.Channels.Telegram;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Features.Audit;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Features.Help;
using AgentsTracker.Gateway.Features.Monitor;
using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Cli;
using AgentsTracker.Gateway.Infrastructure.Modules;

// Раньше всего: в папке данных лежит сам appsettings.Local.json, поэтому её расположение
// нельзя взять из конфига — только из appsettings.json рядом с exe или из окружения.
AppPaths.UseConfiguredDirectory();

// Единственное место, где хост знает конкретные каналы. Новый канал — свой проект
// с IChatChannelModule и строка здесь. Список нужен уже служебным командам: какие ключи
// в настройках канала секретные, знает только его модуль.
IReadOnlyList<IChatChannelModule> channels = [new TelegramChannelModule()];

// Служебные команды отрабатывают до сборки хоста: они трогают файлы и автозапуск,
// транспорт канала и агент им не нужны.
if (ConsoleCommands.TryRun(args, channels, Console.Out, out var commandExitCode)) return commandExitCode;

// Единственное место, где хост знает конкретных агентов. Новый агент — свой проект
// с IAgentBackendModule и строка здесь.
IReadOnlyList<IAgentBackendModule> agents = [new ClaudeAgentModule()];

// Порядок важен: текстовые обработчики опрашиваются в порядке регистрации, а ChatModule
// ловит всё — ему место последним.
IReadOnlyList<IFeatureModule> modules =
[
    new ApprovalsModule(),
    new SettingsModule(),
    new HelpModule(),
    new AuditModule(),
    new MonitorModule(),
    new ChatModule(),
];

// Content root — папка exe, а не текущая папка процесса: запуск из другого места
// (Start-Process, ярлык) иначе молча теряет appsettings.json.
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

// Канал — тоже до сборки контейнера и по тому же образцу.
var channelId = builder.Configuration[ChannelConfiguration.TypeKey] ?? ChannelOptions.DefaultType;
var channel = channels.FirstOrDefault(c => c.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase));
if (channel is null)
{
    Console.Error.WriteLine(
        $"{ChannelConfiguration.TypeKey} = '{channelId}'. Известные каналы: {string.Join(", ", channels.Select(c => c.Id))}.");
    return 1;
}

builder.AddGatewayInfrastructure(agent, channel, modules);

var app = builder.Build();

if (app.ValidateStartup() is not 0 and var exitCode) return exitCode;

app.MapFeatures(agent, channel, modules);

await app.RunAsync();

// Ненулевой код ставит ChatGatewayService, если не смог подключиться: по нему Планировщик
// перезапустит задачу.
return Environment.ExitCode;
