using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Polly.Timeout;

namespace AgentsTracker.Agents.Claude;

/// <summary>
/// Окна тарифа (5 часов, неделя, неделя на модель) с <c>api.anthropic.com/api/oauth/usage</c>:
/// не даёт запустить агента на исчерпанном окне, иначе тариф молча уедет на платные кредиты.
/// Эндпоинт недокументирован — его же зовёт CLI для <c>/usage</c>. Токен берём готовый из
/// <c>~/.claude/.credentials.json</c> или CLAUDE_CODE_OAUTH_TOKEN, заново не логинимся.
/// </summary>
public sealed class ClaudeLimits(IHttpClientFactory httpClientFactory, ILogger<ClaudeLimits> logger) : IAgentLimits
{
    /// <summary>Имя клиента в <see cref="IHttpClientFactory"/>; регистрирует <see cref="ClaudeAgentModule"/>.</summary>
    public const string HttpClientName = "claude-limits";

    /// <summary>
    /// Полный URL в каждом запросе, а не BaseAddress: адрес берётся на момент вызова,
    /// а не запекается в клиента при сборке.
    /// </summary>
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>
    /// Без User-Agent «как у CLI» эндпоинт отвечает 429. Версия фиксированная: настоящую
    /// узнать можно только запуском claude --version.
    /// </summary>
    public const string UserAgent = "claude-code/2.1.260 (external, cli)";

    /// <summary>Кэш: у эндпоинта жёсткий rate limit, частый опрос упирается в 429.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(3);

    /// <summary>Одна попытка и не дольше: проверка стоит перед каждым запуском.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly SemaphoreSlim _gate = new(1, 1);

    private LimitsSnapshot? _cached;
    private DateTimeOffset _lastFetch;

    /// <summary>
    /// Причина отказа, если окно исчерпано, иначе null. model — модель запуска: недельное
    /// окно отдельной модели блокирует только её.
    /// </summary>
    public async Task<string?> RefusalAsync(string? model, CancellationToken ct)
    {
        var snapshot = await GetAsync(ct);

        // Эндпоинт может отвалиться в любой момент; отказывать на каждую задачу — значит
        // замолчать целиком, поэтому запуск пропускаем.
        if (snapshot.Error is { } error)
        {
            logger.LogWarning("Лимиты тарифа не проверены: {Error}", error);
            return null;
        }

        // Кредиты включаются на аккаунте, а не флагом CLI: пока они включены, на них может
        // уехать любая сессия, не только шлюзовая.
        if (snapshot.ExtraUsage is { IsEnabled: true })
            logger.LogWarning("На аккаунте включены кредиты (extra usage) — выключите их в claude.ai → Settings → Usage");

        // Снимку до трёх минут, поэтому окно с прошедшим сбросом уже не считается.
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
    /// Если окон нет, строка пустая: показывать нечего.
    /// </summary>
    public async Task<string> ShortSummaryAsync(string? model, CancellationToken ct)
    {
        var snapshot = await GetAsync(ct);
        if (snapshot.Error is { } error) return error;

        var parts = Live(snapshot, model).Select(w => $"{Describe(w.Key)} {Left(w.Used)}");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Окна для шкал: расход и подпись сброса. Подпись собирает агент, а не хост — формат
    /// времени тут такая же часть представления, как и названия окон.
    /// </summary>
    public async Task<LimitsView> ViewAsync(string? model, CancellationToken ct)
    {
        var snapshot = await GetAsync(ct);
        if (snapshot.Error is { } error) return new LimitsView([], error);

        return new LimitsView(
        [
            .. Live(snapshot, model).Select(w => new LimitGauge(
                Describe(w.Key),
                Math.Clamp(w.Used, 0.0, 1.0),
                w.ResetsAt,
                w.ResetsAt is { } at ? Moment(at) : null))
        ], null);
    }

    /// <summary>
    /// Окна, действующие на следующий запуск: без просроченных и без чужих моделей,
    /// ближайший сброс первым.
    /// </summary>
    private static IEnumerable<LimitWindow> Live(LimitsSnapshot snapshot, string? model) =>
        snapshot.Windows
            .Where(w => Applies(w.Key, model) && !Passed(w.ResetsAt))
            .OrderBy(w => w.ResetsAt ?? DateTimeOffset.MaxValue);

    /// <summary>
    /// Остаток окна в процентах: 100 минус расход, округлённый вверх (чтобы не обнадёживать) —
    /// той же формулой, что и шкалы <c>LimitBars</c>. Своё округление вниз расходилось с ними на
    /// 14 значениях из 1001: у 0.67 доля остатка в double равна 0.32999999999999996, и в меню
    /// выходило «неделя 32%» против «осталось 33%» в том же `/status`. Поправка 1e-9 гасит ту же
    /// погрешность в другую сторону: без неё 0.67 даёт 67.00000000000001 и остаток 32%.
    /// </summary>
    private static string Left(double used) =>
        (100 - (int)Math.Ceiling(Math.Clamp(used, 0.0, 1.0) * 100 - 1e-9)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Окно без модели в ключе действует на любой запуск, окно модели — только на её запуск.
    /// Сравнение по вхождению: «fable» ⊂ «claude-fable-5-1». Если модель не выбрана, её
    /// выберет CLI — считаем только общие окна.
    /// </summary>
    private static bool Applies(string key, string? model)
    {
        if (Suffix(key) is not { } suffix) return true;

        return model is { Length: > 0 }
            && model.Replace('_', '-').Contains(suffix.Replace('_', '-'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Сброс уже прошёл — окно из устаревшего снимка запуск не держит.</summary>
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
    /// «через 2 ч 10 мин (19:40)», как в панели usage у Claude Code: относительное время
    /// отвечает на «сколько ждать», абсолютное в скобках — на «когда возвращаться».
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
            // Клиент из фабрики на каждый запрос: она меняет обработчик по расписанию, и смена
            // DNS у api.anthropic.com не требует перезапуска шлюза.
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
        // InvalidOperationException — чтение поля не того вида. Ответ недокументирован, любой
        // его сдвиг должен пропустить проверку, а не уронить запуск.
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
        // Таймаут конвейера Polly — это TimeoutRejectedException; своя отмена (ct) сюда
        // не попадает и уходит вызывающему.
        catch (TimeoutRejectedException)
        {
            return new LimitsSnapshot(
                [], null, DateTimeOffset.UtcNow, $"api.anthropic.com не ответил за {RequestTimeout.TotalSeconds:0} секунд");
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
    /// Токен подписки. Обновлять его шлюз не пытается: это дело Claude Code, а две стороны,
    /// пишущие в .credentials.json, затрут друг друга.
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
    /// Берём всё, что похоже на окно (five_hour, seven_day, seven_day_&lt;модель&gt;), а не
    /// фиксированный список: набор моделей меняется вместе с тарифами.
    /// </summary>
    private static (IReadOnlyList<LimitWindow> Windows, ExtraUsageState? ExtraUsage) Parse(string body)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.ValueKind is not JsonValueKind.Object) return ([], null);

        var raws = new List<(string Key, double Utilization, DateTimeOffset? ResetsAt)>();
        ExtraUsageState? extra = null;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Не окно, а расход сверх тарифа: без сброса и с другой шкалой.
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

            raws.Add((property.Name, raw, resets));
        }

        // Шкала — одна на весь ответ, поэтому окна собираем только после разбора всех.
        var percents = LooksLikePercents(raws.Select(r => r.Utilization));

        return ([.. raws.Select(r => new LimitWindow(r.Key, Fraction(r.Utilization, percents), r.ResetsAt))], extra);
    }

    private static ExtraUsageState? ParseExtraUsage(JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Object) return null;

        var enabled = value.TryGetProperty("is_enabled", out var flag) && flag.ValueKind is JsonValueKind.True;

        return new ExtraUsageState(enabled, Number(value, "used_credits"));
    }

    /// <summary>
    /// Числовое поле или <c>null</c>. Вид проверяем сами: <c>TryGetDouble</c> на JSON-null
    /// не возвращает false, а бросает <see cref="InvalidOperationException"/>, и одно пустое
    /// поле сорвало бы разбор всего ответа.
    /// </summary>
    private static double? Number(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out var field)
        && field.ValueKind is JsonValueKind.Number
        && field.TryGetDouble(out var value)
            ? value
            : null;

    private static double Fraction(double raw, bool percents) => percents ? raw / 100.0 : raw;

    /// <summary>
    /// Шкала <c>utilization</c> выбирается на весь ответ сразу, а не для каждого окна отдельно:
    /// в одном окне значение 1 не отличить от доли 1.0, и «1%» через пять минут после сброса
    /// читалось как «окно выбрано полностью» — 10.09.2026 бот час отказывал на пустом пятичасовом
    /// окне («сброс в 21:19», хотя окно началось в 16:19). Эндпоинт отдаёт проценты, но набор полей
    /// недокументирован: значение больше 1 или все значения целые — проценты, дробные — доли.
    /// Ошибка в сторону процентов лишь пропустит запуск, ошибка в сторону долей глушит бота.
    /// </summary>
    private static bool LooksLikePercents(IEnumerable<double> values)
    {
        var all = values.ToList();

        return all.Any(v => v > 1.0) || all.TrueForAll(v => v == Math.Truncate(v));
    }

    private static bool IsWindow(string key) =>
        key.StartsWith("five_hour", StringComparison.Ordinal) || key.StartsWith("seven_day", StringComparison.Ordinal);

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
