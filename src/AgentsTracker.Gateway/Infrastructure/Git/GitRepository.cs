using System.Collections.Concurrent;
using System.Text;

namespace AgentsTracker.Gateway.Infrastructure.Git;

/// <summary>Ветка проекта на момент чтения. Ровно одно из Branch и DetachedSha не null.</summary>
/// <param name="WorktreeOf">Имя основной папки репозитория, если проект — worktree; иначе null.</param>
public sealed record GitHead(string? Branch, string? DetachedSha, string? WorktreeOf)
{
    /// <summary>Длиннее имя растягивает строку статуса запуска на две.</summary>
    private const int LabelLength = 40;

    /// <summary>
    /// Подпись для людей — единственное место, где ветка становится текстом. Экранирование под
    /// разметку — на вызывающем: в имени ветки git разрешает «&lt;», «&amp;», «_» и «`».
    /// </summary>
    public string Label => Branch is { } branch
        ? branch.Length > LabelLength ? Cut(branch) + "…" : branch
        : $"{DetachedSha} (без ветки)";

    /// <summary>Не рвёт суррогатную пару: половина эмодзи — невалидный UTF-16, и канал отклонил бы сообщение.</summary>
    private static string Cut(string branch)
    {
        var end = LabelLength - 1;
        return char.IsHighSurrogate(branch[end - 1]) ? branch[..(end - 1)] : branch[..end];
    }
}

/// <summary>
/// Ветка проекта — из файлов <c>.git</c>, без запуска git: подпись нужна на каждой отрисовке и
/// каждом такте статуса, а процесс на каждый такт — это таймауты и <c>safe.directory</c>. Ничего
/// не бросает: при любой ошибке строки с веткой просто нет — сбой здесь не должен стоить ответа
/// агента или статуса запуска.
/// </summary>
public sealed class GitRepository(ILogger<GitRepository> logger)
{
    /// <summary>HEAD — одна короткая строка; больше читать незачем, даже если путь подменён.</summary>
    private const int MaxFileBytes = 1024;

    private const string BranchPrefix = "ref: refs/heads/";
    private const string PointerPrefix = "gitdir:";

    /// <summary>Содержимое по пути, пока у файла те же время записи и длина.</summary>
    private readonly ConcurrentDictionary<string, (DateTime Written, long Length, string Content)> _files =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ветка папки проекта; null — не git, сетевой диск или файл не прочитался.</summary>
    public GitHead? Head(string projectPath)
    {
        try
        {
            return Locate(projectPath) is { } found ? ReadHead(found.GitDir, found.WorktreeOf) : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ветка {Project} не прочитана", projectPath);
            return null;
        }
    }

    /// <summary>
    /// Ищет <c>.git</c> от папки проекта вверх, как сам git: проект бывает подпапкой репозитория,
    /// и подпись совпадает с тем, что увидит git агента в этой папке.
    /// </summary>
    private (string GitDir, string? WorktreeOf)? Locate(string projectPath)
    {
        var start = ProjectCatalog.Normalize(projectPath);

        // Удалённая папка проекта (worktree убрали) — не повод показывать ветку той, что выше.
        if (!IsLocal(start) || !Directory.Exists(start)) return null;

        for (var directory = start; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var dotGit = Path.Combine(directory, ".git");

            if (Directory.Exists(dotGit)) return (dotGit, null);
            if (File.Exists(dotGit)) return FromPointer(dotGit, directory);
        }

        return null;
    }

    /// <summary>
    /// Файл <c>.git</c> — у worktree и подмодуля. Worktree отличает <c>commondir</c> в его
    /// папке: у подмодуля общего репозитория нет, и подписывать его worktree было бы неправдой.
    /// </summary>
    private (string GitDir, string? WorktreeOf)? FromPointer(string dotGit, string directory)
    {
        if (ReadSmall(dotGit) is not { } content || !content.StartsWith(PointerPrefix, StringComparison.Ordinal))
            return null;

        if (Resolve(content[PointerPrefix.Length..], directory) is not { } gitDir || !Directory.Exists(gitDir))
            return null;

        if (ReadSmall(Path.Combine(gitDir, "commondir")) is not { } common) return (gitDir, null);

        return Resolve(common, gitDir) is { } commonDir
            ? (gitDir, Path.GetFileName(Path.GetDirectoryName(commonDir)))
            : null;
    }

    /// <summary>
    /// Путь из файла репозитория. Его пишет тот, кто дал проект: UNC-путь заставил бы Windows
    /// подключиться к чужому серверу и отдать ему хэш учётной записи, а сетевая папка без ответа
    /// повесила бы отрисовку меню. Относительный путь (<c>worktree.useRelativePaths</c>)
    /// считается от папки файла.
    /// </summary>
    private static string? Resolve(string pointer, string baseDirectory)
    {
        var trimmed = pointer.Trim();
        if (trimmed.Length == 0 || IsUnc(trimmed)) return null;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed, baseDirectory));
        return IsLocal(full) ? full : null;
    }

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>Не UNC и не сетевой диск: обращение к такой папке может отдать чужому серверу хэш учётной записи.</summary>
    public static bool IsLocal(string path)
    {
        if (IsUnc(path)) return false;
        if (!OperatingSystem.IsWindows()) return true;

        // Подключённый сетевой диск («Z:») — та же сетевая папка, только с буквой.
        return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType != DriveType.Network;
    }

    /// <summary>
    /// HEAD: имя ветки или SHA. Всё остальное — не показываем: <c>gitdir</c> мог указать на чужую
    /// папку, и первая строка её файла HEAD попала бы в чат.
    /// </summary>
    private GitHead? ReadHead(string gitDir, string? worktreeOf)
    {
        if (ReadSmall(Path.Combine(gitDir, "HEAD")) is not { } content) return null;

        var line = content.Split('\n', 2)[0].Trim();

        if (line.StartsWith(BranchPrefix, StringComparison.Ordinal))
        {
            var branch = line[BranchPrefix.Length..];
            return branch.Length > 0 && !branch.Any(char.IsControl) ? new GitHead(branch, null, worktreeOf) : null;
        }

        return line.Length is 40 or 64 && line.All(char.IsAsciiHexDigitLower)
            ? new GitHead(null, line[..7], worktreeOf)
            : null;
    }

    /// <summary>
    /// Читает маленький файл, не мешая git. <c>git checkout</c> на Windows переименовывает
    /// <c>HEAD.lock</c> поверх <c>HEAD</c>: открытый без <c>FileShare.Delete</c> файл сорвал бы
    /// переименование, и команда агента упала бы — без терминала git не переспрашивает. Поэтому
    /// файл открывается только после смены времени записи или длины (их Windows отдаёт без
    /// открытия), а открывается со всеми режимами совместного доступа.
    /// </summary>
    private string? ReadSmall(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            _files.TryRemove(path, out _);
            return null;
        }

        var written = file.LastWriteTimeUtc;
        var length = file.Length;

        if (_files.TryGetValue(path, out var cached) && cached.Written == written && cached.Length == length)
            return cached.Content;

        string content;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);

            var buffer = new byte[MaxFileBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            content = Encoding.UTF8.GetString(buffer, 0, read).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Посреди переименования файла может не оказаться: прошлое значение вернее пустого.
            logger.LogDebug(ex, "Не прочитан {Path}", path);
            return cached.Content;
        }

        _files[path] = (written, length, content);
        return content;
    }
}
