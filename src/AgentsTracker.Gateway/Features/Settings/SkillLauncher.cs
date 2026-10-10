using System.Collections.Concurrent;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Запуск скилла из меню и ожидание аргументов к нему. Кнопка «С аргументами» запоминает
/// команду за пользователем, а следующий его текст уходит агенту как «/команда текст» —
/// иначе аргументы пришлось бы набирать вместе с длинным именем плагинного скилла.
/// </summary>
public sealed class SkillLauncher(SessionStore store, ChatWorker worker, IAuditLog audit)
{
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

        return wasBusy ? $"📥 В очереди: {text}" : $"🚀 Запуск: {text}";
    }

    public void Expect(UserId user, string command) => _pending[user] = command;

    public bool TryTake(UserId user, out string command) => _pending.TryRemove(user, out command!);

    public void Cancel(UserId user) => _pending.TryRemove(user, out _);
}
