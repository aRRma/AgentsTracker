using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Chat;

namespace AgentsTracker.Gateway.Features.Chat;

/// <summary>Что удалось принять и что сказать пользователю про остальное.</summary>
/// <param name="Paths">Абсолютные пути принятых картинок в порядке отправки.</param>
/// <param name="Refusal">Текст отказа для чата; null — принято всё.</param>
public sealed record AttachmentBatch(IReadOnlyList<string> Paths, string? Refusal);

/// <summary>
/// Входящие файлы: скачивает, проверяет и хранит. Зеркало <c>OperatorConsole.SendFileAsync</c>
/// для обратного направления — вся политика приёма собрана здесь, а не размазана по каналу
/// и обработчику.
///
/// Раскладка: <c>&lt;папка данных&gt;\inbox\&lt;чат&gt;\&lt;проект&gt;\</c>. Агенту открывается
/// (<c>--add-dir</c>) только папка чата: в самой папке данных лежат state.json, локальный
/// конфиг с секретами и токен MCP. Подпапка проекта нужна, чтобы <c>/new</c> в одном проекте
/// не стирал картинки другого.
/// </summary>
public sealed class AttachmentInbox(
    IChatChannel channel,
    SessionStore store,
    IAuditLog audit,
    IOptions<GatewayOptions> options,
    ILogger<AttachmentInbox> logger)
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>Сколько байт хватает, чтобы отличить PNG от JPEG.</summary>
    private const int SignatureLength = 8;

    private readonly AttachmentOptions _options = options.Value.Attachments;

    private static string Root => Path.Combine(AppPaths.DataDirectory, "inbox");

    /// <summary>Папка чата — её и только её видит агент.</summary>
    public string ChatDirectory(ChatId chat) => Path.Combine(Root, Safe(chat.Key));

    /// <summary>Папка проекта внутри чата: её содержимое живёт ровно столько, сколько сессия.</summary>
    public string SessionDirectory(ChatId chat, string projectPath) =>
        Path.Combine(ChatDirectory(chat), ProjectFolder(projectPath));

    /// <summary>
    /// Скачивает вложения текущего сообщения. Отказ — всегда текстом, а не исключением:
    /// пользователь должен понять, почему его картинка не попала к агенту.
    /// </summary>
    public async Task<AttachmentBatch> StoreAsync(
        ChatId chat, UserId user, IReadOnlyList<IncomingAttachment> attachments, CancellationToken ct)
    {
        if (attachments.Count == 0) return new AttachmentBatch([], null);

        if (!_options.Enabled)
            return new AttachmentBatch([], "Приём вложений выключен в настройках шлюза (Gateway:Attachments:Enabled).");

        var project = store.ProjectPath;
        var directory = SessionDirectory(chat, project);

        // Чистим здесь же: шлюз работает неделями, и одного обхода при старте мало,
        // чтобы «храним сутки» оставалось правдой.
        Sweep(ChatDirectory(chat));

        var paths = new List<string>();
        var refusals = new List<string>();

        foreach (var attachment in attachments)
        {
            var stored = await StoreOneAsync(chat, user, project, directory, attachment, ct);

            if (stored.Path is { Length: > 0 } path) paths.Add(path);
            else if (stored.Refusal is { Length: > 0 } refusal && !refusals.Contains(refusal)) refusals.Add(refusal);
        }

        return new AttachmentBatch(paths, refusals.Count > 0 ? "⚠️ " + string.Join("\n", refusals) : null);
    }

    private async Task<(string? Path, string? Refusal)> StoreOneAsync(
        ChatId chat, UserId user, string project, string directory, IncomingAttachment attachment, CancellationToken ct)
    {
        // Тип проверяем по байтам после скачивания, но голос и видео качать незачем:
        // канал уже сказал, что это не картинка.
        if (attachment.Kind == AttachmentKind.Other)
            return (null, Refuse(chat, user, project, "не картинка", "Понимаю только картинки: PNG и JPEG."));

        var limit = Math.Min(_options.MaxBytes, channel.Limits.AttachmentBytes);

        if (attachment.Size is { } size && size > limit)
        {
            return (null, Refuse(chat, user, project, $"{size.Bytes} > {limit.Bytes}",
                $"Картинка слишком большая: {size.Bytes}, предел — {limit.Bytes}."));
        }

        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Path.GetRandomFileName());

        try
        {
            await DownloadAsync(attachment, temp, limit, ct);

            var extension = await SniffAsync(temp, ct);
            if (extension is null)
            {
                Delete(temp);
                return (null, Refuse(chat, user, project, "тип не опознан", "Понимаю только картинки: PNG и JPEG."));
            }

            var name = $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}{extension}";
            var path = Path.Combine(directory, name);
            File.Move(temp, path);

            var length = new FileInfo(path).Length;
            logger.LogInformation("Принята картинка {Name} ({Size})", name, length.Bytes);
            audit.Write(AuditEvent.Now(AuditKinds.FileReceive, $"{name} ({length.Bytes})", user, chat, project, store.SessionId));

            return (path, null);
        }
        catch (Exception ex) when (IsTooLarge(ex))
        {
            Delete(temp);
            return (null, Refuse(chat, user, project, $"больше {limit.Bytes}", $"Картинка слишком большая, предел — {limit.Bytes}."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Delete(temp);
            throw;
        }
        catch (Exception ex)
        {
            Delete(temp);
            logger.LogWarning(ex, "Не удалось принять вложение");
            return (null, Refuse(chat, user, project, ex.Message, $"Не удалось скачать вложение: {ex.Message}"));
        }
    }

    /// <summary>
    /// Превышение потолка ищем по всей цепочке: канал заворачивает исключение потока-приёмника
    /// в своё (у Telegram.Bot это «Exception during file download»), и по типу верхнего
    /// исключения отказ по размеру не отличить от обрыва связи.
    /// </summary>
    private static bool IsTooLarge(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is AttachmentTooLargeException) return true;
        }

        return false;
    }

    private async Task DownloadAsync(IncomingAttachment attachment, string temp, long limit, CancellationToken ct)
    {
        // Asynchronous — иначе на Windows запись идёт синхронно и держит поток пула всю загрузку.
        await using var file = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        await using var capped = new CappedStream(file, limit);
        await channel.DownloadAttachmentAsync(attachment, capped, ct);
    }

    /// <summary>
    /// Тип — по сигнатуре первых байт. Заявленному MIME и имени файла верить нельзя: и то,
    /// и другое пишет отправитель, а расширение решает, увидит агент картинку или мусор.
    /// </summary>
    private static async Task<string?> SniffAsync(string path, CancellationToken ct)
    {
        var head = new byte[SignatureLength];

        await using var file = File.OpenRead(path);
        var read = await file.ReadAtLeastAsync(head, SignatureLength, throwOnEndOfStream: false, ct);

        var signature = head.AsSpan(0, read);

        if (signature.StartsWith(PngSignature)) return ".png";
        if (signature.StartsWith(JpegSignature)) return ".jpg";

        return null;
    }

    /// <summary>Картинки закрытой сессии: контекст забыт, файлам тоже незачем оставаться.</summary>
    public void ClearSession(ChatId chat, string projectPath)
    {
        var directory = SessionDirectory(chat, projectPath);
        if (!Directory.Exists(directory)) return;

        // Сначала файлы, потом папка: только что удалённый файл Windows держит ещё мгновение,
        // и Directory.Delete падает на нём с «папка не пуста». Картинок к этому моменту уже
        // нет, а пустую папку уберёт ближайшая чистка.
        foreach (var file in Files(directory)) Delete(file);

        RemoveEmpty(directory);
    }

    /// <summary>Обход всего inbox при старте: файлы прошлых суток переживают перезапуск.</summary>
    public void SweepStale() => Sweep(Root);

    private void Sweep(string directory)
    {
        if (!Directory.Exists(directory)) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(_options.RetentionHours);

        foreach (var file in Files(directory))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff) Delete(file);
        }

        // Снизу вверх: иначе внешняя папка проверяется, пока внутренняя ещё на месте.
        foreach (var folder in Folders(directory).Reverse()) RemoveEmpty(folder);
    }

    private IReadOnlyList<string> Files(string directory) => Enumerate(() => Directory.GetFiles(directory, "*", SearchOption.AllDirectories));

    private IReadOnlyList<string> Folders(string directory) => Enumerate(() => Directory.GetDirectories(directory, "*", SearchOption.AllDirectories));

    /// <summary>Папку могли удалить между обходом и обращением: чистка не повод падать.</summary>
    private IReadOnlyList<string> Enumerate(Func<string[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось прочитать папку вложений");
            return [];
        }
    }

    private void RemoveEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Пустая папка вреда не делает: её уберёт следующая чистка.
            logger.LogDebug(ex, "Папка вложений осталась: {Directory}", directory);
        }
    }

    private void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Не удалось удалить файл вложения");
        }
    }

    /// <summary>Отказ тоже в журнал: попытка прислать чужой файл важнее удачного приёма.</summary>
    private string Refuse(ChatId chat, UserId user, string project, string reason, string reply)
    {
        logger.LogInformation("Вложение отклонено: {Reason}", reason);
        audit.Write(AuditEvent.Now(AuditKinds.FileReceive, reason, user, chat, project, store.SessionId, "refused"));
        return reply;
    }

    /// <summary>
    /// Папка проекта: имя плюс хвост хеша полного пути. Одно имя встречается в разных корнях
    /// (worktree рядом с основной папкой), а разъезжаться их сессии не должны.
    /// </summary>
    private static string ProjectFolder(string projectPath)
    {
        var full = ProjectCatalog.Normalize(projectPath);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8];

        return Safe(Path.GetFileName(full)) + "-" + hash;
    }

    /// <summary>
    /// Имя папки из ключа адреса: в «telegram:123» двоеточие Windows не примет. Имена самих
    /// файлов сюда не попадают — их придумывает шлюз, а не отправитель.
    /// </summary>
    private static string Safe(string value)
    {
        var name = new StringBuilder(value.Length);

        foreach (var symbol in value)
            name.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), symbol) < 0 ? symbol : '_');

        return name.Length > 0 ? name.ToString() : "chat";
    }

    private sealed class AttachmentTooLargeException : Exception;

    /// <summary>
    /// Поток с потолком: заявленный размер — слова отправителя, а канал пишет сюда, пока
    /// не кончится файл. Обрываем на превышении, недокачанный кусок удаляет вызывающий.
    /// </summary>
    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long _written;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Account(buffer.Length);
            inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Account(buffer.Length);
            await inner.WriteAsync(buffer, ct);
        }

        private void Account(int count)
        {
            _written += count;
            if (_written > limit) throw new AttachmentTooLargeException();
        }
    }
}
