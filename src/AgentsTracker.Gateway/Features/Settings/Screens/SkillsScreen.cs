using System.Security.Cryptography;
using System.Text;
using AgentsTracker.Gateway.Infrastructure.Claude;
using Telegram.Bot.Types.ReplyMarkups;
using static AgentsTracker.Gateway.Features.Settings.SettingsKeyboard;

namespace AgentsTracker.Gateway.Features.Settings.Screens;

/// <summary>
/// Скиллы Claude Code кнопками: источник (встроенные, проект, личные, плагин) → скилл →
/// карточка с описанием, подсказкой по аргументам и флагами. Из карточки скилл запускается
/// сразу или после ввода аргументов следующим сообщением (<see cref="SkillLauncher"/>).
/// </summary>
public sealed class SkillsScreen(SessionStore store, SkillCatalog catalog, SkillLauncher launcher) : ISettingsScreen
{
    private const int PageSize = 12;

    /// <summary>Префиксы callback-ов. Ключи скиллов и групп — 12 hex-символов, с буквами префиксов не пересекаются
    /// только потому, что после префикса всегда идёт ключ той же длины или число.</summary>
    private const string PagePrefix = "p";

    private const string GroupPrefix = "g";

    private const string CardPrefix = "c";

    private const string RunPrefix = "r";

    private const string AskPrefix = "a";

    private const string UpArgument = "up";

    private const string ListArgument = "list";

    /// <summary>Сколько частых скиллов выносить в отдельную группу наверх.</summary>
    private const int TopCount = 5;

    private const string TopGroup = "⭐ Частые";

    /// <summary>Открытая группа, страница и карточка — поля экрана: меню одно на шлюз, как и в ProjectScreen.</summary>
    private string? _group;

    private int _page;

    private string? _card;

    public string Key => "skills";

    public string? Apply(string argument, long userId, long chatId)
    {
        switch (argument)
        {
            case UpArgument:
                _group = null;
                _page = 0;
                _card = null;
                return null;

            case ListArgument:
                _card = null;
                launcher.Cancel(userId);
                return null;
        }

        if (argument.StartsWith(GroupPrefix, StringComparison.Ordinal))
        {
            _group = argument[GroupPrefix.Length..];
            _page = 0;
            _card = null;
            return null;
        }

        if (argument.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(argument[PagePrefix.Length..], out var page))
        {
            _page = page;
            return null;
        }

        if (argument.StartsWith(CardPrefix, StringComparison.Ordinal))
        {
            _card = argument[CardPrefix.Length..];
            return null;
        }

        if (argument.StartsWith(RunPrefix, StringComparison.Ordinal))
        {
            var skill = Find(argument[RunPrefix.Length..]);
            if (skill is null) return "Скилла уже нет в списке";

            launcher.Cancel(userId);
            return launcher.Launch(chatId, userId, skill.Command);
        }

        if (argument.StartsWith(AskPrefix, StringComparison.Ordinal))
        {
            var skill = Find(argument[AskPrefix.Length..]);
            if (skill is null) return "Скилла уже нет в списке";

            launcher.Expect(userId, skill.Command);
            return $"Напишите аргументы для {skill.Command} следующим сообщением";
        }

        return null;
    }

    public Task<(string Html, InlineKeyboardMarkup Keyboard)> RenderAsync(CancellationToken ct) =>
        Task.FromResult(Render());

    /// <summary>Ищем по ключу, а не по номеру: между отрисовкой и нажатием список мог измениться.</summary>
    private SkillInfo? Find(string key) =>
        catalog.Grouped(store.ProjectPath).SelectMany(g => g.Skills).FirstOrDefault(s => SkillKey(s.Command) == key);

    private (string Html, InlineKeyboardMarkup Keyboard) Render()
    {
        // Пересобирается на каждый показ: плагин могли включить или выключить в терминале.
        var groups = WithTop(catalog.Grouped(store.ProjectPath));

        if (groups.Count == 0)
        {
            var empty = """
                🧩 <b>Скиллы</b>

                Ничего не найдено: ни <code>.claude/skills</code> в проекте и профиле,
                ни включённых плагинов в <code>~/.claude/plugins</code>, ни строк
                в <code>Gateway:BuiltInSkills</code>.

                <i>Неизвестные шлюзу слэш-команды и так уходят агенту как есть.</i>
                """;
            return (empty, new InlineKeyboardMarkup([[BackButton]]));
        }

        if (_card is not null)
        {
            var skill = groups.SelectMany(g => g.Skills).FirstOrDefault(s => SkillKey(s.Command) == _card);
            if (skill is not null) return RenderCard(skill);
            _card = null;
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

    /// <summary>
    /// Первой группой — до пяти самых запускаемых скиллов из каталога; счётчики ведёт
    /// <see cref="SessionStore.RecordSkillUse"/>. Остальные группы остаются по алфавиту:
    /// частый скилл виден и там, чтобы в списке источника не было «дыр».
    /// </summary>
    private IReadOnlyList<SkillGroup> WithTop(IReadOnlyList<SkillGroup> groups)
    {
        var usage = store.SkillUsage();
        if (usage.Count == 0) return groups;

        var top = groups
            .SelectMany(g => g.Skills)
            .Select(s => (Skill: s, Count: usage.GetValueOrDefault(s.Command)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Skill.Command, StringComparer.OrdinalIgnoreCase)
            .Take(TopCount)
            .Select(x => x.Skill)
            .ToList();

        return top.Count == 0 ? groups : [new SkillGroup(TopGroup, top), .. groups];
    }

    private (string Html, InlineKeyboardMarkup Keyboard) RenderGroups(IReadOnlyList<SkillGroup> groups)
    {
        var (page, counter, pageRow) = Page(groups, "источников");

        var lines = page.Select(group => $"· <b>{E(group.Name)}</b> — {group.Skills.Count}");

        var html = $"""
            🧩 <b>Скиллы</b>

            {string.Join("\n", lines)}

            <i>Выберите источник, потом скилл: откроется карточка с описанием,
            аргументами и кнопкой запуска.</i>{counter}
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
        var usage = store.SkillUsage();

        var lines = page.Select(skill =>
            $"· <code>{E(skill.Command)}</code>"
            + (usage.GetValueOrDefault(skill.Command) is > 0 and var count ? $" — {count} {Times(count)}" : "")
            + (skill.Description.Length > 0 ? $"\n   {E(skill.Description)}" : ""));

        var html = $"""
            🧩 <b>{E(group.Name)}</b>

            {string.Join("\n", lines)}

            <i>Нажатие открывает карточку скилла.</i>{counter}
            """;

        // В кнопке — имя без префикса плагина: он и так в заголовке, а место в кнопке дорого.
        // В группе частых источники разные, там префикс остаётся — иначе два одноимённых не различить.
        var mixed = group.Name == TopGroup;
        var buttons = page
            .Select(skill => Button(mixed ? skill.Command : ShortName(skill.Command), $"skills:{CardPrefix}{SkillKey(skill.Command)}"))
            .Chunk(2)
            .ToList();

        if (pageRow is not null) buttons.Add(pageRow);
        buttons.Add(single ? [BackButton] : [Button("📦 К источникам", $"skills:{UpArgument}"), BackButton]);

        return (html, new InlineKeyboardMarkup(buttons));
    }

    /// <summary>
    /// Карточка: всё, что известно о скилле до запуска. Формальной схемы аргументов у скиллов
    /// нет, поэтому показываем то, что удалось достать — подсказку из frontmatter и флаги,
    /// упомянутые в тексте.
    /// </summary>
    private (string Html, InlineKeyboardMarkup Keyboard) RenderCard(SkillInfo skill)
    {
        var count = store.SkillUsage().GetValueOrDefault(skill.Command);
        var key = SkillKey(skill.Command);

        var parts = new List<string> { $"🧩 <b>{E(skill.Command)}</b>  <i>({E(skill.Group)})</i>" };

        if (skill.Details.Length > 0) parts.Add(E(skill.Details));

        var usage = new List<string>();
        if (skill.ArgumentHint is not null) usage.Add($"Аргументы: <code>{E(skill.Command)} {E(skill.ArgumentHint)}</code>");
        if (skill.Flags.Count > 0) usage.Add($"Флаги, упомянутые в описании: {string.Join(", ", skill.Flags.Select(f => $"<code>{E(f)}</code>"))}");
        if (usage.Count == 0) usage.Add("<i>Подсказки по аргументам нет — обычно запускается без них.</i>");
        if (count > 0) usage.Add($"Запускали: {count} {Times(count)}");
        parts.Add(string.Join("\n", usage));

        parts.Add("<i>«Запустить» — без аргументов. «С аргументами» — следующее сообщение станет аргументами команды; «отмена» отменяет.</i>");

        var html = string.Join("\n\n", parts);

        var keyboard = new InlineKeyboardMarkup(
        [
            [Button("🚀 Запустить", $"skills:{RunPrefix}{key}"), Button("✏️ С аргументами", $"skills:{AskPrefix}{key}")],
            [Button("◀️ К списку", $"skills:{ListArgument}"), BackButton],
        ]);

        return (html, keyboard);
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

    /// <summary>«раз» / «раза» по правилам русского: 1, 21 — раз; 2–4, 22–24 — раза; 5–20, 25–30 — раз.</summary>
    private static string Times(int count)
    {
        var tens = count % 100;
        var ones = count % 10;
        return ones is >= 2 and <= 4 && tens is < 12 or > 14 ? "раза" : "раз";
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
