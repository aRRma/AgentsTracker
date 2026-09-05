using System.Security.Cryptography;
using System.Text;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Claude;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Скиллы Claude Code кнопками: сначала источник (проект, личные, плагин), потом скилл.
/// Нажатие ставит в очередь агента слэш-команду — ту же, что пользователь набрал бы руками.
/// </summary>
public sealed class SkillsScreen(
    SessionStore store,
    SkillCatalog catalog,
    ChatWorker worker,
    IAuditLog audit) : ISettingsScreen
{
    private const int PageSize = 12;

    /// <summary>Префиксы перелистывания и открытия группы. Ключи скиллов — hex, не пересекаются.</summary>
    private const string PagePrefix = "p";

    private const string GroupPrefix = "g";

    private const string UpArgument = "up";

    /// <summary>Открытая группа и страница — поля экрана: меню одно на шлюз, как и в ProjectScreen.</summary>
    private string? _group;

    private int _page;

    public string Key => "skills";

    public string? Apply(string argument, long userId, long chatId)
    {
        if (argument == UpArgument)
        {
            _group = null;
            _page = 0;
            return null;
        }

        if (argument.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            _group = argument[GroupPrefix.Length..];
            _page = 0;
            return null;
        }

        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _page = page;
            return null;
        }

        // Ищем по ключу, а не по номеру: между отрисовкой и нажатием список мог измениться.
        var skill = catalog.Grouped(store.ProjectPath)
            .SelectMany(g => g.Skills)
            .FirstOrDefault(s => SkillKey(s.Command) == argument);

        if (skill is null) return "Скилла уже нет в списке";

        audit.Write(AuditEvent.Now(
            AuditKinds.Message, $"skill: {skill.Command}", userId, chatId, store.ProjectPath, store.SessionId));

        var wasBusy = worker.IsBusy;
        worker.Enqueue(chatId, userId, skill.Command);

        var hint = skill.ArgumentHint is null ? "" : $"\nС аргументами: {skill.Command} {skill.ArgumentHint}";
        return (wasBusy ? $"📥 В очереди: {skill.Command}" : $"🚀 Запуск: {skill.Command}") + hint;
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(CancellationToken ct) =>
        Task.FromResult(Render());

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        // Пересобирается на каждый показ: плагин могли включить или выключить в терминале.
        var groups = catalog.Grouped(store.ProjectPath);

        if (groups.Count == 0)
        {
            var empty = """
                🧩 <b>Скиллы</b>

                Ничего не найдено: ни <code>.claude/skills</code> в проекте и профиле,
                ни включённых плагинов в <code>~/.claude/plugins</code>.

                <i>Встроенные команды Claude Code (например <code>/init</code>) можно набрать вручную —
                неизвестные шлюзу слэш-команды уходят агенту как есть.</i>
                """;
            return (empty, new InlineKeyboardMarkup([[BackButton]]));
        }

        if (groups.Count == 1) return RenderSkills(groups[0], single: true);

        var opened = _group is null ? null : groups.FirstOrDefault(g => GroupKey(g.Name) == _group);

        if (opened is null)
        {
            _group = null;
            return RenderGroups(groups);
        }

        return RenderSkills(opened, single: false);
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderGroups(IReadOnlyList<SkillGroup> groups)
    {
        var (page, counter, pageRow) = Page(groups, "источников");

        var lines = page.Select(group => $"· <b>{E(group.Name)}</b> — {group.Skills.Count}");

        var html = $"""
            🧩 <b>Скиллы</b>

            {string.Join("\n", lines)}

            <i>Выберите источник, потом скилл. Нажатие запускает его в текущей сессии;
            скилл с аргументами наберите руками: <code>/имя аргументы</code>.</i>{counter}
            """;

        var buttons = page
            .Select(group => Button($"📦 {group.Name} ({group.Skills.Count})", $"skills:{GroupPrefix}{GroupKey(group.Name)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add([BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderSkills(SkillGroup group, bool single)
    {
        var (page, counter, pageRow) = Page(group.Skills, "скиллов");

        var lines = page.Select(skill =>
            $"· <code>{E(skill.Command)}{(skill.ArgumentHint is null ? "" : " " + E(skill.ArgumentHint))}</code>"
            + (skill.Description.Length > 0 ? $"\n   {E(skill.Description)}" : ""));

        var html = $"""
            🧩 <b>{E(group.Name)}</b>

            {string.Join("\n", lines)}

            <i>Нажатие запускает скилл в текущей сессии без аргументов.</i>{counter}
            """;

        // В кнопке — имя без префикса плагина: он и так в заголовке, а место в кнопке дорого.
        var buttons = page
            .Select(skill => Button(ShortName(skill.Command), $"skills:{SkillKey(skill.Command)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add(single ? [BackButton] : [Button("📦 К источникам", $"skills:{UpArgument}"), BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    private (T[] Items, string Counter, InlineKeyboardButton[]? PageRow) Page<T>(IReadOnlyList<T> all, string noun)
    {
        var pages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, pages - 1);

        var items = all.Skip(_page * PageSize).Take(PageSize).ToArray();
        if (pages == 1) return (items, "", null);

        var counter = $"{Environment.NewLine}{Environment.NewLine}Страница {_page + 1} из {pages}, всего {noun}: {all.Count}.";

        InlineKeyboardButton[] row =
        [
            Button("◀", $"skills:{PagePrefix}{(_page - 1 + pages) % pages}"),
            Button($"{_page + 1}/{pages}", $"skills:{PagePrefix}{_page}"),
            Button("▶", $"skills:{PagePrefix}{(_page + 1) % pages}"),
        ];

        return (items, counter, row);
    }

    private static string ShortName(string command)
    {
        var colon = command.IndexOf(':');
        return colon < 0 ? command : "/" + command[(colon + 1)..];
    }

    /// <summary>Короткий ключ для callback_data: полная команда плагина в 64 байта может не влезть.</summary>
    private static string SkillKey(string command) => Key12(command);

    private static string GroupKey(string group) => Key12(group);

    private static string Key12(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..12];
}
