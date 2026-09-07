namespace AgentsTracker.Channels;

/// <summary>Кнопка под сообщением: подпись и данные, которые вернутся в <see cref="ButtonPress.Data"/>.</summary>
public sealed record KeyboardButton(string Label, string Data);

/// <summary>Кнопки рядами: <c>new Keyboard([[a, b], [c]])</c>. Пустая клавиатура — сообщение без кнопок.</summary>
public sealed record Keyboard(IReadOnlyList<IReadOnlyList<KeyboardButton>> Rows);

/// <summary>
/// Что отправить или чем заменить сообщение. <paramref name="Rich"/> — текст в каноническом
/// формате (<see cref="ChatHtml"/>), иначе как есть, без разметки.
/// </summary>
public sealed record OutgoingMessage(string Text, bool Rich = true, Keyboard? Keyboard = null);

/// <summary>Отправленное сообщение: по нему канал его правит и удаляет.</summary>
public sealed record MessageRef(ChatId Chat, string Id);

/// <summary>
/// Тип чата с точки зрения хоста. <see cref="Unknown"/> — канал не смог определить (у Telegram
/// callback без сообщения); хост обязан считать это отказом, а не личным чатом.
/// </summary>
public enum ChatKind
{
    Direct,
    Group,
    Unknown,
}

/// <summary>Текст от пользователя. Проверку доступа делает хост, канал только сообщает, откуда пришло.</summary>
public sealed record IncomingMessage(ChatId Chat, UserId User, string Text, ChatKind Kind);

/// <summary>
/// Нажатие кнопки. <paramref name="PressId"/> нужен, чтобы ответить каналу («принято»,
/// всплывающий текст); <paramref name="Message"/> — сообщение с кнопками, если канал его
/// отдал (может не отдать: старое или недоступное).
/// </summary>
public sealed record ButtonPress(
    string PressId,
    ChatId Chat,
    UserId User,
    string Data,
    MessageRef? Message,
    ChatKind Kind);

/// <summary>Команда шлюза для подсказки в интерфейсе канала (кнопка «Меню» у Telegram).</summary>
public sealed record ChatCommand(string Name, string Description);
