using System.Collections.Concurrent;
using System.Text;

namespace AgentsTracker.Gateway.Infrastructure.Git;

/// <summary>Ветка проекта на момент чтения. Ровно одно из Branch и DetachedSha не null.</summary>
/// <param name="WorktreeOf">Имя основной папки репозитория, если проект — worktree; иначе null.</param>
public sealed record GitHead(string? Branch, string? DetachedSha, string? WorktreeOf)
{
    /// <summary>Длиннее имя не влезет в строку статуса и во всплывающее уведомление (200 знаков).</summary>
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

/// <param name="Path">Папка worktree.</param>
public sealed record GitWorktree(string Path, GitHead Head);

/// <summary>
/// Ветка и worktree проекта — из файлов <c>.git</c>, без запуска git: подпись нужна на каждой
/// отрисовке и каждом такте статуса, а процесс на каждый такт — это таймауты и
/// <c>safe.directory</c>. Ничего не бросает: при любой ошибке строки с веткой просто нет —
/// сбой здесь не должен стоить ответа агента или статуса запуска.
/// </summary>
public sealed class GitRepository(IOptions<GatewayOptions> options, ILogger<GitRepository> logger)
{
    /// <summary>HEAD — одна короткая строка; больше читать незачем, даже если путь подменён.</summary>
    private const int MaxFileBytes = 1024;

    private const string BranchPrefix = "ref: refs/heads/";
    private const string PointerPrefix = "gitdir:";

    /// <summary>Содержимое по пути, пока у файла те же время записи и длина.</summary>
    private readonly ConcurrentDictionary<string, (DateTime Written, long Length, string Content)> _files =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record Location(string GitDir, string CommonDir, string? WorktreeOf);

    /// <summary>Ветка папки проекта; null — не git, сетевой диск или файл не прочитался.</summary>
    public GitHead? Head(string projectPath)
    {
        try
        {
            return Locate(projectPath) is { } location ? ReadHead(location.GitDir, location.WorktreeOf) : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ветка {Project} не прочитана", projectPath);
            return null;
        }
    }

    /// <summary>
    /// Worktree репозитория проекта, кроме основной папки. Удалённые с диска без
    /// <c>git worktree prune</c> пропускаются: их метаданные остаются в <c>.git\worktrees</c>.
    /// </summary>
    public IReadOnlyList<GitWorktree> Worktrees(string projectPath)
    {
        try
        {
            if (Locate(projectPath) is not { } location) return [];

            var registry = Path.Combine(location.CommonDir, "worktrees");
            if (!Directory.Exists(registry)) return [];

            var main = Path.GetFileName(Path.GetDirectoryName(location.CommonDir));
            var found = new List<GitWorktree>();

            // Без EnumerationOptions: с ними по умолчанию пропускается скрытое, а на Windows
            // git ставит атрибут «скрытый» на служебные папки.
            foreach (var entry in Directory.GetDirectories(registry))
            {
                // Одна битая запись (NUL в пути, файл исчез между проверкой и чтением) не должна
                // стоить всего списка.
                try
                {
                    if (ReadSmall(Path.Combine(entry, "gitdir")) is not { } pointer) continue;
                    if (Resolve(pointer, entry) is not { } dotGit) continue;

                    var folder = Path.GetDirectoryName(dotGit);
                    if (folder is null || !Directory.Exists(folder)) continue;

                    if (ReadHead(entry, main) is { } head) found.Add(new GitWorktree(folder, head));
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
                {
                    logger.LogDebug(ex, "Запись worktree {Entry} не прочитана", entry);
                }
            }

            found.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            return found;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Worktree {Project} не прочитаны", projectPath);
            return [];
        }
    }

    /// <summary>
    /// Ищет <c>.git</c> от папки проекта вверх: проект из Gateway:Projects бывает подпапкой
    /// репозитория. Подъём кончается на корне диска или на Gateway:ProjectsRoot — выше корня
    /// поиска лежит чужое, например репозиторий настроек в профиле.
    /// </summary>
    private Location? Locate(string projectPath)
    {
        var start = ProjectCatalog.Normalize(projectPath);

        // Удалённая папка проекта (worktree убрали) — не повод показывать ветку той, что выше.
        if (!IsLocal(start) || !Directory.Exists(start)) return null;

        var ceiling = options.Value.ProjectsRoot is { Length: > 0 } root ? ProjectCatalog.Normalize(root) : null;

        for (var directory = start; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var dotGit = Path.Combine(directory, ".git");

            if (Directory.Exists(dotGit)) return new Location(dotGit, dotGit, null);
            if (File.Exists(dotGit)) return FromPointer(dotGit, directory);

            if (ceiling is not null && ProjectCatalog.Same(directory, ceiling)) break;
        }

        return null;
    }

    /// <summary>
    /// Файл <c>.git</c> — у worktree и подмодуля. Worktree отличает <c>commondir</c> в его
    /// папке: у подмодуля общего репозитория нет, и подписывать его worktree было бы неправдой.
    /// </summary>
    private Location? FromPointer(string dotGit, string directory)
    {
        if (ReadSmall(dotGit) is not { } content || !content.StartsWith(PointerPrefix, StringComparison.Ordinal))
            return null;

        if (Resolve(content[PointerPrefix.Length..], directory) is not { } gitDir || !Directory.Exists(gitDir))
            return null;

        if (ReadSmall(Path.Combine(gitDir, "commondir")) is not { } common)
            return new Location(gitDir, gitDir, null);

        if (Resolve(common, gitDir) is not { } commonDir) return null;

        return new Location(gitDir, commonDir, Path.GetFileName(Path.GetDirectoryName(commonDir)));
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

    private static bool IsLocal(string path)
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
            return IsBranchName(branch) ? new GitHead(branch, null, worktreeOf) : null;
        }

        return line.Length is 40 or 64 && line.All(char.IsAsciiHexDigitLower)
            ? new GitHead(null, line[..7], worktreeOf)
            : null;
    }

    /// <summary>Правила имени ветки из <c>git check-ref-format</c> — те, что видны в одной строке.</summary>
    private static bool IsBranchName(string name) =>
        name.Length > 0
        && !name.Any(c => c < ' ' || c == '\x7f' || " ~^:?*[\\".Contains(c))
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.Contains("@{", StringComparison.Ordinal)
        && !name.EndsWith('/') && !name.EndsWith('.')
        && !name.EndsWith(".lock", StringComparison.Ordinal);

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
