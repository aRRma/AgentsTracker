namespace AgentsTracker.Gateway.Domain;

/// <summary>
/// Шаг идущего запуска, о котором стоит сказать в чате: какой инструмент агент вызвал
/// и с чем. Без этого долгий запуск выглядит зависшим — пользователь видит только «Работаю…».
/// </summary>
/// <param name="Description">Строка для статуса: «Read ChatWorker.cs», «Bash dotnet build».</param>
/// <param name="ToolCalls">Сколько инструментов вызвано с начала запуска.</param>
/// <param name="Nested">Шаг сабагента: показывается с отступом, чтобы не путать с основным ходом.</param>
public sealed record RunActivity(string Description, int ToolCalls, bool Nested = false);
