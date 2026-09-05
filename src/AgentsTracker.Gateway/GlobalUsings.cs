// Слои, на которые опирается любой файл проекта: контракты агентов, чистые модели,
// техническая часть и конфиг. Остальные пространства подключаются точечно там, где нужны.
global using AgentsTracker.Agents;
global using AgentsTracker.Gateway.Domain;
global using AgentsTracker.Gateway.Infrastructure;
global using AgentsTracker.Gateway.Infrastructure.Configuration;
global using AgentsTracker.Gateway.Infrastructure.State;
global using Microsoft.Extensions.Options;
