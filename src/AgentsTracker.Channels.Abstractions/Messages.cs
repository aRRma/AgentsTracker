namespace AgentsTracker.Channels;

/// <summary>Кнопка под сообщением: подпись и данные, которые вернутся в <see cref="ButtonPress.Data"/>.</summary>
public sealed record KeyboardButton(string Label, string Data);

/// <summary>Кнопки рядами: <c>new Keyboard([[a, b], [c]])</c>. Пустая — сообщение без кнопок.</summary>
public sealed record Keyboard(IReadOnlyList<IReadOnlyList<KeyboardButton>> Rows);

/// <summary>
/// Что отправить или чем заменить сообщение. <paramref name="Rich"/> — текст в формате
/// <see cref="ChatHtml"/>, иначе как есть, без разметки.
/// </summary>
public sealed record OutgoingMessage(string Text, bool Rich = true, Keyboard? Keyboard = null);

/// <summary>Отправленное сообщение: по нему канал его правит и удаляет.</summary>
public sealed record MessageRef(ChatId Chat, string Id);

/// <summary>
/// Тип чата глазами хоста. <see cref="Unknown"/> — канал не определил (в Telegram это
/// callback без сообщения); хост обязан считать такое отказом, а не личным чатом.
/// </summary>
public enum ChatKind
{
    Direct,
    Group,
    Unknown,
}

/// <summary>
/// Что за вложение пришло. <see cref="Photo"/> — картинка, пережатая мессенджером;
/// <see cref="Document"/> — файл как есть, с именем и заявленным типом; <see cref="Other"/> —
/// голос, видео, стикер и прочее, чего хост принимать не умеет: он ответит отказом, а не
/// молчанием.
/// </summary>
public enum AttachmentKind
{
    Photo,
    Document,
    Other,
}

/// <summary>
/// Вложение до скачивания. Канал отдаёт только описание, тело — по запросу хоста
/// (<see cref="IChatChannel.DownloadAttachmentAsync"/>): куда его класть и надолго ли,
/// решает хост.
/// </summary>
/// <param name="FileId">Ключ вложения у канала — по нему хост просит тело.</param>
/// <param name="FileName">Имя от отправителя; у фото его не бывает. Доверять ему нельзя.</param>
/// <param name="MimeType">Тип по версии отправителя — тоже лишь подсказка.</param>
/// <param name="Size">Размер по версии канала; null — канал его не сообщил.</param>
public sealed record IncomingAttachment(
    string FileId,
    AttachmentKind Kind,
    string? FileName,
    string? MimeType,
    long? Size);

/// <summary>
/// Сообщение от пользователя. Доступ проверяет хост, канал лишь говорит, откуда пришло.
/// <paramref name="Text"/> пуст, если прислали вложение без подписи.
/// </summary>
public sealed record IncomingMessage(
    ChatId Chat,
    UserId User,
    string Text,
    ChatKind Kind,
    IReadOnlyList<IncomingAttachment> Attachments);

/// <summary>
/// Нажатие кнопки. <paramref name="PressId"/> — чем ответить каналу («принято», всплывающий
/// текст). <paramref name="Message"/> — сообщение с кнопками, если канал его отдал: старое
/// или недоступное он может и не отдать.
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
