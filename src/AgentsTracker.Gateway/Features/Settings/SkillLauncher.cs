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
    private readonly ConcurrentDictionary<long, string> _pending = new();

    /// <summary>Ставит команду в очередь агента, считает запуск и пишет аудит. Возвращает текст для чата.</summary>
    public string Launch(long chatId, long userId, string command, string? arguments = null)
    {
        var text = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments.Trim()}";

        store.RecordSkillUse(command);
        audit.Write(AuditEvent.Now(
            AuditKinds.Message, $"skill: {text}", userId, chatId, store.ProjectPath, store.SessionId));

        // Занятость проверяется до постановки в очередь — как в ChatEnqueueTextHandler.
        var wasBusy = worker.IsBusy;
        worker.Enqueue(chatId, userId, text);

        return wasBusy ? $"📥 В очереди: {text}" : $"🚀 Запуск: {text}";
    }

    public void Expect(long userId, string command) => _pending[userId] = command;

    public bool TryTake(long userId, out string command) => _pending.TryRemove(userId, out command!);

    public void Cancel(long userId) => _pending.TryRemove(userId, out _);
}
