using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Audit;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Topics;

/// <summary>
/// «Тип темы»: проект или беседа. Новая тема попадает сюда раньше первой задачи — шлюз не
/// угадывает, с чем пришли: задача в чужом проекте хуже одного лишнего нажатия. «Проект» ведёт
/// на обычный выбор проекта, «Беседа» записывает тему без проекта.
/// </summary>
public sealed class TopicScreen(SessionStore store, IAuditLog audit, IOptions<GatewayOptions> options) : ISettingsScreen
{
    public const string ScreenKey = "topic";

    private const string Talk = "talk";

    public string Key => ScreenKey;

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        var scope = store.For(chat);
        if (argument != Talk || !scope.IsTopic) return null;
        if (scope.IsChosenTalk) return "Тема уже беседа";

        var previous = scope.IsNewTopic ? null : Path.GetFileName(scope.ProjectPath);
        scope.SetProjectPath(null);
        audit.Changed(scope, user, "topic", previous, "беседа");
        return "💬 Беседа — пришлите сообщение снова";
    }

    public Task<(string Html, Keyboard Keyboard)> RenderAsync(ChatId chat, CancellationToken ct) =>
        Task.FromResult(Render(store.For(chat)));

    private (string Html, Keyboard Keyboard) Render(SessionStore.ChatScope scope)
    {
        if (!scope.IsTopic)
        {
            return ("""
                🧵 <b>Тип темы</b>

                Тип выбирают в теме. Лента работает с проектом из меню «📁 Проект».
                """, new Keyboard([[BackButton]]));
        }

        var now = scope.IsNewTopic ? "не выбран"
            : scope.IsTalk ? "💬 беседа"
            : $"📁 проект {E(Path.GetFileName(scope.ProjectPath))}";

        var hint = scope.IsNewTopic
            ? "\n\n<i>Задача не запущена: сначала выберите тип темы, потом пришлите её снова — с картинкой, если она была.</i>"
            : "";

        var html = $"""
            🧵 <b>Тип темы</b> — сейчас: {now}

            📁 <b>Проект</b> — задачи в папке проекта: правки, команды, своя сессия.
            💬 <b>Беседа</b> — тема без проекта: только поиск в сети, файлов нет. Модель — {E(options.Value.Question.Model ?? "по умолчанию")}.{hint}
            """;

        var talk = scope.IsChosenTalk;
        return (html, new Keyboard(
        [
            [Button("📁 Проект", "proj"), Button($"{(talk ? "▶ " : "")}💬 Беседа", $"{ScreenKey}:{Talk}")],
            [BackButton],
        ]));
    }
}
