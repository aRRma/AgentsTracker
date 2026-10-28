using System.Collections.Concurrent;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;

namespace AgentsTracker.Gateway.Features.Settings;

/// <summary>
/// Запуск скилла из меню и ожидание аргументов к нему. Кнопка «С аргументами» запоминает
/// команду за чатом, а следующий текст в нём уходит агенту как «/команда текст» —
/// иначе аргументы пришлось бы набирать вместе с длинным именем плагинного скилла.
/// </summary>
public sealed class SkillLauncher(SessionStore store, ChatWorker worker, IAuditLog audit)
{
    /// <summary>
    /// Что ждём где: чат → команда. По чату, а не по пользователю: чаты личные, а текст в
    /// соседней теме — уже задача её проекта, а не аргументы скилла отсюда.
    /// </summary>
    private readonly ConcurrentDictionary<ChatId, string> _pending = new();

    /// <summary>Ставит команду в очередь агента, считает запуск и пишет аудит. Возвращает текст для чата.</summary>
    public string Launch(ChatId chat, UserId user, string command, string? arguments = null)
    {
        var text = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments.Trim()}";
        var scope = store.For(chat);

        // Как и текст: без типа темы скилл ушёл бы беседой, без файлов, хотя звали его к проекту.
        if (scope.IsNewTopic) return "Сначала выберите тип темы: /topic";

        store.RecordSkillUse(command);
        audit.Write(AuditEvent.Now(
            AuditKinds.Message, $"skill: {text}", user, chat, scope.ProjectPath, scope.SessionId));

        // Занятость проверяется до постановки в очередь — как в ChatEnqueueTextHandler.
        var wasBusy = worker.IsBusy(chat);
        worker.Enqueue(chat, user, text);

        return wasBusy ? $"📥 В очереди: {text}" : $"🚀 Запуск: {text}";
    }

    public void Expect(ChatId chat, string command) => _pending[chat] = command;

    public bool TryTake(ChatId chat, out string command) => _pending.TryRemove(chat, out command!);

    public void Cancel(ChatId chat) => _pending.TryRemove(chat, out _);
}
