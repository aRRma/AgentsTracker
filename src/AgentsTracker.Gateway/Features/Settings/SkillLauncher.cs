using System.Collections.Concurrent;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Git;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Запуск скилла из меню и ожидание аргументов к нему. Кнопка «С аргументами» запоминает
/// команду за пользователем, а следующий его текст уходит агенту как «/команда текст» —
/// иначе аргументы пришлось бы набирать вместе с длинным именем плагинного скилла.
/// </summary>
public sealed class SkillLauncher(SessionStore store, ChatWorker worker, GitRepository git, IAuditLog audit)
{
    /// <summary>Предел всплывающего уведомления Telegram: длиннее — 400, и уведомление теряется.</summary>
    private const int ToastLength = 200;

    /// <summary>Что ждём от кого: пользователь → команда. Ждём по пользователю, а не по чату: чаты личные.</summary>
    private readonly ConcurrentDictionary<UserId, string> _pending = new();

    /// <summary>Ставит команду в очередь агента, считает запуск и пишет аудит. Возвращает текст для чата.</summary>
    public string Launch(ChatId chat, UserId user, string command, string? arguments = null)
    {
        var text = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments.Trim()}";

        store.RecordSkillUse(command);
        audit.Write(AuditEvent.Now(
            AuditKinds.Message, $"skill: {text}", user, chat, store.ProjectPath, store.SessionId));

        // Занятость проверяется до постановки в очередь — как в ChatEnqueueTextHandler.
        var wasBusy = worker.IsBusy;
        worker.Enqueue(chat, user, text);

        var reply = wasBusy ? $"📥 В очереди: {text}" : $"🚀 Запуск: {text}";

        // Ветка на момент нажатия: скилл ревью без аргумента берёт текущую ветку, и по ней видно,
        // что он проверит. Пока задача ждёт очереди, ветка может смениться — точную покажет
        // статус запуска.
        var branch = git.Head(store.ProjectPath) is { } head ? $" · 🌿 {head.Label}" : "";
        return reply.Length + branch.Length <= ToastLength ? reply + branch : reply;
    }

    public void Expect(UserId user, string command) => _pending[user] = command;

    public bool TryTake(UserId user, out string command) => _pending.TryRemove(user, out command!);

    public void Cancel(UserId user) => _pending.TryRemove(user, out _);
}
