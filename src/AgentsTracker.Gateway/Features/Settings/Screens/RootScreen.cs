using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Features.Question;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Корневой экран: сводка и переходы к остальным. Кнопки — по частоте использования:
/// сначала то, что нужно в каждой сессии (статус, сессии), потом настройки агента и
/// скиллы, в конце проект и расход — их трогают раз в день.
/// </summary>
public sealed class RootScreen(
    SessionStore store, IAgentBackend agent, ChatWorker worker, IAgentLimits limits, QuestionLauncher question) : ISettingsScreen
{
    public string Key => "root";

    /// <summary>Строка «где» у новой темы: «беседа» там соврала бы — задача до выбора типа не пойдёт.</summary>
    public const string NewTopic = "🧵 <b>Тип темы не выбран</b> — /topic";

    public string? Apply(string argument, UserId user, ChatId chat) => null;

    // «Назад» с экрана «Вопрос» ведёт сюда: ожидание вопроса снимаем, иначе следующая
    // задача ушла бы вопросом мимо сессии.
    public void Open(ChatId chat) => question.Cancel(chat);

    public async Task<(string Html, Keyboard Keyboard)> RenderAsync(ChatId chat, CancellationToken ct)
    {
        var scope = store.For(chat);
        var project = scope.ProjectPath;
        var session = store.Session(scope.SessionId);

        // Остаток тарифа, а не потраченные доллары: на подписке платить не за что,
        // а упереться можно только в окно лимита.
        var plan = await limits.ShortSummaryAsync(scope.EffectiveModel, ct);

        // Строку effort показываем только агенту, который его понимает.
        var effort = agent.Capabilities.Effort is not null
            ? $"\n🎚 Effort: <b>{E(scope.EffectiveEffort ?? "по умолчанию")}</b>"
            : "";

        var where = ScopeLabel.Where(scope, project);

        // Вопрос идёт параллельно задаче: без своей строки «простаивает» при идущем вопросе
        // выглядело бы так, будто ответа не будет.
        var questions = worker.IsAnsweringFor(chat)
            ? $"\n💬 Идёт вопрос{(worker.QuestionQueueLength > 0 ? $", ещё в очереди: {worker.QuestionQueueLength}" : "")}"
            : "";

        var html = $"""
            ⚙️ <b>Настройки</b> — {E(agent.DisplayName)}

            {where}
            🧠 Модель: <b>{E(store.ModelLabel(scope.EffectiveModel, agent.Capabilities.Model) ?? "по умолчанию")}</b>{effort}
            🔐 Режим: <b>{E(scope.EffectivePermissionMode)}</b>
            🧵 Сессия: {(session is null ? "<i>новая</i>" : $"<b>{E(session.Title)}</b>")}
            📦 Контекст: {(session is { ContextTokens: > 0, ContextWindow: > 0 } ? ContextScreen.Fill(session, bar: false) : "—")}
            🚦 Осталось: <b>{E(plan.Length > 0 ? plan : "—")}</b>
            ⚙️ {(worker.IsBusy(chat) ? "выполняется" : "простаивает")}, в очереди: {worker.QueueLength(chat)}{questions}
            """;

        // «Тип темы» — только в теме: в ленте выбирать нечего.
        var keyboard = new Keyboard(
        [
            [Button("📟 Статус", "status"), Button("🧵 Сессии", "sess")],
            [Button("📦 Контекст", "ctx"), Button("💬 Задать вопрос", "ask")],
            chat.IsTopic ? [Button("🤖 Агент", "agent"), Button("🧵 Тип темы", "topic")] : [Button("🤖 Агент", "agent")],
            [Button("🧩 Скиллы", "skills"), Button("📁 Проект", "proj")],
            [Button("📊 Расход", "usage"), Button("✖️ Закрыть", "close")],
        ]);

        return (html, keyboard);
    }
}
