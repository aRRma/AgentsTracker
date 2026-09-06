using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Следит за лимитами тарифа Claude (пятичасовое окно, недельные — общее и на отдельные модели)
/// эндпоинтом <c>api.anthropic.com/api/oauth/usage</c> и не даёт запустить агента, когда окно
/// выбрано до конца: исчерпанный тариф иначе молча переходит на платные кредиты.
///
/// Эндпоинт недокументирован — им пользуется сам CLI для <c>/usage</c>, наружу CLI эти данные
/// не отдаёт ни командой, ни флагом. Токен подписки не запрашивается заново: берётся тот,
/// что Claude Code держит в <c>~/.claude/.credentials.json</c> (или из CLAUDE_CODE_OAUTH_TOKEN).
/// </summary>
public sealed class ClaudeLimits(IHttpClientFactory httpClientFactory, ILogger<ClaudeLimits> logger) : IAgentLimits
{
    /// <summary>Имя клиента в <see cref="IHttpClientFactory"/>; регистрирует <see cref="ClaudeAgentModule"/>.</summary>
    public const string HttpClientName = "claude-limits";

    /// <summary>
    /// Полный URL в каждом запросе, не BaseAddress: адрес разрешается на момент вызова, а не
    /// на момент сборки клиента, и не переживает переезд эндпоинта.
    /// </summary>
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>
    /// Эндпоинт отвечает 429 всем, кто не похож на CLI, поэтому User-Agent обязателен.
    /// Версия здесь фиксированная: узнать настоящую можно только запуском claude --version.
    /// </summary>
    public const string UserAgent = "claude-code/2.1.260 (external, cli)";

    /// <summary>Кэш: у эндпоинта жёсткий rate limit, частый опрос упирается в 429.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(3);

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly SemaphoreSlim _gate = new(1, 1);

    private LimitsSnapshot? _cached;
    private DateTimeOffset _lastFetch;

    /// <summary>
    /// Причина отказа, если тарифное окно исчерпано, иначе null. model — модель, которой пойдёт
    /// запуск: недельное окно отдельной модели блокирует только её.
    /// </summary>
    public async Task<string?> RefusalAsync(string? model, CancellationToken ct)
    {
        var snapshot = await GetAsync(ct);

        // Эндпоинт недокументирован и может отвалиться в любой момент. Отказывать тогда на каждую
        // задачу было бы хуже — шлюз замолчал бы целиком, поэтому запуск пропускаем.
        if (snapshot.Error is { } error)
        {
            logger.LogWarning("Лимиты тарифа не проверены: {Error}", error);
            return null;
        }

        // Кредиты включаются на аккаунте, а не флагом CLI: пока они включены, исчерпанный тариф
        // может уехать на них в любой сессии, не только в шлюзовой. Об этом стоит знать.
        if (snapshot.ExtraUsage is { IsEnabled: true })
            logger.LogWarning("На аккаунте включены кредиты (extra usage) — выключите их в claude.ai → Settings → Usage");

        // Снимок живёт до трёх минут: окно, чей срок сброса уже прошёл, больше не держит.
        var window = snapshot.Windows
            .Where(w => w.Used >= 1.0 && Applies(w.Key, model) && !Passed(w.ResetsAt))
            .OrderBy(w => w.ResetsAt ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();

        if (window is null) return null;

        logger.LogWarning(
            "Запуск отклонён: окно {Window} выбрано на {Used:P0}, сброс {ResetsAt}",
            window.Key, window.Used, window.ResetsAt);

        var reset = window.ResetsAt is { } at ? $" Сброс {Moment(at)}." : "";

        var credits = snapshot.ExtraUsage is { IsEnabled: true }
            ? "\n\nНа аккаунте включены кредиты («extra usage») — шлюз их не тратит, но интерактивные "
              + "сессии Claude Code смогут. Выключить: claude.ai → Settings → Usage."
            : "";

        return $"🚦 Лимит тарифа исчерпан: окно «{Describe(window.Key)}» выбрано полностью.{reset}\n\n"
             + $"Задача не запущена — кредиты шлюз не тратит. Повторите после сброса.{credits}";
    }

    public async Task<LimitsSnapshot> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - _lastFetch < CacheFor) return cached;

            // Отметку ставим до запроса: неудача должна тормозить опрос так же, как удача.
            _lastFetch = DateTimeOffset.UtcNow;
            _cached = await FetchAsync(ct);

            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Остаток окон одной строкой — «5 часов 66% · неделя 88%» — для шапки меню и /status.
    /// Пусто, если окон нет: показывать нечего, а строка «—» только зашумит сводку.
    /// </summary>
    public async Task<string> ShortSummaryAsync(string? model, CancellationToken ct)
    {
        var snapshot = await GetAsync(ct);
        if (snapshot.Error is { } error) return error;

        var parts = Live(snapshot, model).Select(w => $"{Describe(w.Key)} {Left(w.Used)}");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Окна для шкал: остаток и подпись сброса. Подпись собирается здесь, а не в хосте:
    /// формат времени сброса — часть представления агента, как и названия окон.
    /// </summary>
    public async Task<LimitsView> ViewAsync(string? model, CancellationToken ct)
    {
        var snapshot = await GetAsync(ct);
        if (snapshot.Error is { } error) return new LimitsView([], error);

        return new LimitsView(
        [
            .. Live(snapshot, model).Select(w => new LimitGauge(
                Describe(w.Key),
                Math.Clamp(1.0 - w.Used, 0.0, 1.0),
                w.ResetsAt,
                w.ResetsAt is { } at ? Moment(at) : null))
        ], null);
    }

    /// <summary>
    /// Окна, которые действуют на следующий запуск: без просроченных и без чужих моделей.
    /// Порядок — по времени сброса, чтобы ближайшее было первым.
    /// </summary>
    private static IEnumerable<LimitWindow> Live(LimitsSnapshot snapshot, string? model) =>
        snapshot.Windows
            .Where(w => Applies(w.Key, model) && !Passed(w.ResetsAt))
            .OrderBy(w => w.ResetsAt ?? DateTimeOffset.MaxValue);

    /// <summary>Остаток окна в процентах. Округляем вниз: «1%» честнее, чем обнадёживающий «2%».</summary>
    private static string Left(double used) =>
        ((int)Math.Floor(Math.Clamp(1.0 - used, 0.0, 1.0) * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Окна без модели в ключе действуют на любой запуск; окно отдельной модели — только когда
    /// запуск пойдёт этой моделью. Сравниваем по вхождению: «fable» ⊂ «claude-fable-5-1».
    /// Когда модель не выбрана, её выбирает CLI — тогда учитываем только общие окна.
    /// </summary>
    private static bool Applies(string key, string? model)
    {
        if (Suffix(key) is not { } suffix) return true;

        return model is { Length: > 0 }
            && model.Replace('_', '-').Contains(suffix.Replace('_', '-'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Срок сброса окна уже прошёл: снимок устарел, держать запуск больше нечем.</summary>
    private static bool Passed(DateTimeOffset? resetsAt) => resetsAt is { } at && at <= DateTimeOffset.UtcNow;

    private static string? Suffix(string key) => key switch
    {
        _ when key.StartsWith("seven_day_", StringComparison.Ordinal) => key["seven_day_".Length..],
        _ when key.StartsWith("five_hour_", StringComparison.Ordinal) => key["five_hour_".Length..],
        _ => null,
    };

    private static string Describe(string key) => key switch
    {
        "five_hour" => "5 часов",
        "seven_day" => "неделя",
        _ when Suffix(key) is { } suffix => $"неделя, {suffix.Replace('_', ' ')}",
        _ => key,
    };

    /// <summary>
    /// «через 2 ч 10 мин», как в панели usage самого Claude Code: относительное время
    /// отвечает на вопрос «сколько ждать», абсолютное заставляет считать в уме.
    /// Точное время — в скобках, оно нужно, чтобы спланировать возвращение.
    /// </summary>
    private static string Moment(DateTimeOffset moment)
    {
        var left = moment - DateTimeOffset.UtcNow;
        var local = moment.ToLocalTime();

        var relative = left switch
        {
            { TotalMinutes: < 1 } => "меньше минуты",
            { TotalHours: < 1 } => $"{(int)left.TotalMinutes} мин",
            { TotalDays: < 1 } => $"{(int)left.TotalHours} ч {left.Minutes} мин",
            _ => $"{(int)left.TotalDays} д {left.Hours} ч",
        };

        var absolute = local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", Russian)
            : local.ToString("d MMMM HH:mm", Russian);

        return $"через {relative} ({absolute})";
    }

    private async Task<LimitsSnapshot> FetchAsync(CancellationToken ct)
    {
        var (token, problem) = ReadToken();
        if (token is null) return new LimitsSnapshot([], null, DateTimeOffset.UtcNow, problem);

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");

        try
        {
            // Клиент на каждый запрос: фабрика меняет обработчик по расписанию, и смена DNS
            // у api.anthropic.com не требует перезапуска шлюза. Ретраи и таймауты — в конвейере.
            using var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Эндпоинт лимитов ответил {Code}: {Body}", (int)response.StatusCode, Truncate(body));
                return new LimitsSnapshot([], null, DateTimeOffset.UtcNow, Describe(response.StatusCode));
            }

            var (windows, extra) = Parse(body);
            return new LimitsSnapshot(windows, extra, DateTimeOffset.UtcNow, null);
        }
        // InvalidOperationException — это чтение поля не того вида: ответ недокументирован,
        // и любой его сдвиг должен пропустить проверку лимитов, а не уронить запуск.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Не разобран ответ эндпоинта лимитов");
            return new LimitsSnapshot([], null, DateTimeOffset.UtcNow, "ответ эндпоинта не разобран, формат изменился");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Не удалось получить лимиты тарифа");
            return new LimitsSnapshot(
                [], null, DateTimeOffset.UtcNow, $"не достучаться до api.anthropic.com: {ex.Message}");
        }
        // Таймаут конвейера — TimeoutRejectedException, разомкнутый предохранитель —
        // BrokenCircuitException; своя отмена сюда не попадает.
        catch (Exception ex) when (ex is TimeoutRejectedException or BrokenCircuitException)
        {
            logger.LogWarning(ex, "Эндпоинт лимитов недоступен");
            return new LimitsSnapshot([], null, DateTimeOffset.UtcNow, "api.anthropic.com не отвечает");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LimitsSnapshot([], null, DateTimeOffset.UtcNow, "api.anthropic.com не ответил вовремя");
        }
    }

    private static string Describe(HttpStatusCode code) => code switch
    {
        HttpStatusCode.TooManyRequests => "эндпоинт ответил 429, слишком частый опрос",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "токен подписки не принят: запустите claude в терминале, он обновит ~/.claude/.credentials.json",
        HttpStatusCode.NotFound => "эндпоинта больше нет — он недокументирован и может исчезнуть в любой версии",
        _ => $"эндпоинт ответил {(int)code}",
    };

    /// <summary>
    /// Токен подписки. Обновлять его шлюз не пытается: refresh — дело самого Claude Code,
    /// две стороны, переписывающие .credentials.json, легко затрут друг друга.
    /// </summary>
    private static (string? Token, string? Problem) ReadToken()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is { Length: > 0 } fromEnvironment)
            return (fromEnvironment, null);

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

        if (!File.Exists(path))
            return (null, "не найден ~/.claude/.credentials.json — вход в Claude Code, похоже, не по подписке");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var token)
                || token.GetString() is not { Length: > 0 } value)
            {
                return (null, "в ~/.claude/.credentials.json нет токена подписки");
            }

            if (Number(oauth, "expiresAt") is { } milliseconds
                && DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds) <= DateTimeOffset.UtcNow)
            {
                return (null, "токен подписки просрочен: запустите claude в терминале, он обновит его");
            }

            return (value, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, $"не прочитать ~/.claude/.credentials.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Разбирает ответ, не завязываясь на список окон: тариф отдаёт five_hour, seven_day и
    /// seven_day_&lt;модель&gt; (opus, sonnet, fable — набор меняется вместе с тарифами),
    /// поэтому берём всё, что похоже на окно.
    /// </summary>
    private static (IReadOnlyList<LimitWindow> Windows, ExtraUsageState? ExtraUsage) Parse(string body)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.ValueKind is not JsonValueKind.Object) return ([], null);

        var windows = new List<LimitWindow>();
        ExtraUsageState? extra = null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Не окно, а купленный сверх тарифа расход: без сброса и с другой шкалой.
            if (property.Name is "extra_usage")
            {
                extra = ParseExtraUsage(property.Value);
                continue;
            }

            var value = property.Value;

            if (!IsWindow(property.Name) || value.ValueKind is not JsonValueKind.Object) continue;

            // Явный null вместо числа — окна на этом тарифе нет, ограничивать нечего.
            if (Number(value, "utilization") is not { } raw) continue;

            DateTimeOffset? resets =
                value.TryGetProperty("resets_at", out var at)
                && at.ValueKind is JsonValueKind.String
                && DateTimeOffset.TryParse(
                    at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
                    ? moment
                    : null;

            windows.Add(new LimitWindow(property.Name, Fraction(raw), resets));
        }

        return (windows, extra);
    }

    private static ExtraUsageState? ParseExtraUsage(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Object) return null;

        var enabled = value.TryGetProperty("is_enabled", out var flag) && flag.ValueKind is JsonValueKind.True;

        return new ExtraUsageState(enabled, Number(value, "used_credits"));
    }

    /// <summary>
    /// Числовое поле объекта или <c>null</c>, если его нет либо оно не число.
    /// Проверка вида обязательна: <c>TryGetDouble</c> на <c>null</c> не возвращает false,
    /// а бросает <see cref="InvalidOperationException"/> — а её тут ловить некому,
    /// и сбой опроса лимитов превратился бы в отказ всего запуска.
    /// </summary>
    private static double? Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var field)
        && field.ValueKind is JsonValueKind.Number
        && field.TryGetDouble(out var value)
            ? value
            : null;

    /// <summary>
    /// Шкала <c>utilization</c> у Anthropic то доля (0..1), то проценты, и меняться она может
    /// без предупреждения. Всё, что больше единицы, считаем процентами.
    /// </summary>
    private static double Fraction(double raw) => raw > 1.0 ? raw / 100.0 : raw;

    private static bool IsWindow(string key) =>
        key.StartsWith("five_hour", StringComparison.Ordinal) || key.StartsWith("seven_day", StringComparison.Ordinal);

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
