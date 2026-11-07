using AgentsTracker.Gateway.Features.Settings;
using AgentsTracker.Gateway.Infrastructure.Audit;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Topics;

/// <summary>
/// «Тип темы»: проект, беседа или «Работа». Новая тема попадает сюда раньше первой задачи — шлюз не
/// угадывает, с чем пришли: задача в чужом проекте хуже одного лишнего нажатия. «Проект» ведёт
/// на обычный выбор проекта, «Беседа» записывает тему без проекта, «Работа» — тему со всем
/// компьютером, только тем, кому она разрешена в конфиге.
/// </summary>
public sealed class TopicScreen(SessionStore store, WorkAccess work, IAuditLog audit, IOptions<GatewayOptions> options) : ISettingsScreen
{
    public const string ScreenKey = "topic";

    private const string Talk = "talk";
    private const string Work = "work";

    public string Key => ScreenKey;

    public string? Apply(string argument, UserId user, ChatId chat)
    {
        var scope = store.For(chat);
        if (!scope.IsTopic) return null;

        var previous = scope.IsNewTopic ? null : ScopeLabel.Name(scope, scope.ProjectPath);

        switch (argument)
        {
            case Talk:
                if (scope.IsChosenTalk) return "Тема уже беседа";
                scope.SetProjectPath(null);
                audit.Changed(scope, user, "topic", previous, "беседа");
                return "💬 Беседа — пришлите сообщение снова";

            case Work:
                // Кнопку видят все, если тема разрешена хоть кому-то: экран не знает, кто его открыл.
                // Поэтому решает нажатие, а не отрисовка.
                if (!work.Allows(user)) return "⛔ Тема «Работа» вам не разрешена";
                if (scope.IsWork) return "Тема уже «Работа»";
                scope.SetWork(work.PermissionMode);
                audit.Changed(scope, user, "topic", previous, "работа");
                return "💼 Работа — пришлите сообщение снова";

            default:
                return null;
        }
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
            : scope.IsWork ? "💼 работа"
            : $"📁 проект {E(Path.GetFileName(scope.ProjectPath))}";

        var hint = scope.IsNewTopic
            ? "\n\n<i>Задача не запущена: сначала выберите тип темы, потом пришлите её снова — с картинкой, если она была.</i>"
            : "";

        var workLine = work.Enabled
            ? $"\n💼 <b>Работа</b> — весь компьютер: команды, файлы, сеть, своя папка. Режим {E(work.PermissionMode)}, всё вне ваших правил — карточкой, кнопки «Всегда» нет."
            : "";

        var html = $"""
            🧵 <b>Тип темы</b> — сейчас: {now}

            📁 <b>Проект</b> — задачи в папке проекта: правки, команды, своя сессия.
            💬 <b>Беседа</b> — тема без проекта: только поиск в сети, файлов нет. Модель — {E(options.Value.Question.Model ?? "по умолчанию")}.{workLine}{hint}
            """;

        List<KeyboardButton> row =
        [
            Button("📁 Проект", "proj"),
            Button($"{(scope.IsChosenTalk ? "▶ " : "")}💬 Беседа", $"{ScreenKey}:{Talk}"),
        ];
        if (work.Enabled) row.Add(Button($"{(scope.IsWork ? "▶ " : "")}💼 Работа", $"{ScreenKey}:{Work}"));

        return (html, new Keyboard([row, [BackButton]]));
    }
}
