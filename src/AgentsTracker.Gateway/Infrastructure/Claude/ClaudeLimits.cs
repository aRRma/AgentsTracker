using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentsTracker.Gateway.Infrastructure.Claude;

/// <summary>
/// Окно лимита тарифа. Key — ключ из ответа: <c>five_hour</c>, <c>seven_day</c> или
/// <c>seven_day_&lt;модель&gt;</c>. Used — доля израсходованного окна, 0..1.
/// </summary>
public sealed record LimitWindow(string Key, double Used, DateTimeOffset? ResetsAt);

/// <summary>Состояние кредитов («extra usage») на аккаунте: их шлюз тратить не даёт.</summary>
public sealed record ExtraUsageState(bool IsEnabled, double? UsedCredits);

/// <summary>Ответ эндпоинта лимитов либо причина, по которой его не удалось получить.</summary>
public sealed record LimitsSnapshot(
    IReadOnlyList<LimitWindow> Windows,
    ExtraUsageState? ExtraUsage,
    DateTimeOffset FetchedUtc,
    string? Error);

/// <summary>
/// Следит за лимитами тарифа Claude (пятичасовое окно, недельные — общее и на отдельные модели)
/// эндпоинтом <c>api.anthropic.com/api/oauth/usage</c> и не даёт запустить агента, когда окно
/// выбрано до конца: исчерпанный тариф иначе молча переходит на платные кредиты.
///
/// Эндпоинт недокументирован — им пользуется сам CLI для <c>/usage</c>, наружу CLI эти данные
/// не отдаёт ни командой, ни флагом. Токен подписки не запрашивается заново: берётся тот,
/// что Claude Code держит в <c>~/.claude/.credentials.json</c> (или из CLAUDE_CODE_OAUTH_TOKEN).
/// </summary>
public sealed class ClaudeLimits(IOptions<GatewayOptions> options, ILogger<ClaudeLimits> logger)
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>
    /// Эндпоинт отвечает 429 всем, кто не похож на CLI, поэтому User-Agent обязателен.
    /// Версия здесь фиксированная: узнать настоящую можно только запуском claude --version.
    /// </summary>
    private const string UserAgent = "claude-code/2.1.260 (external, cli)";

    /// <summary>Кэш: у эндпоинта жёсткий rate limit, частый опрос упирается в 429.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(3);

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly HttpClient _http = CreateClient(options.Value);
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

    private static string Moment(DateTimeOffset moment)
    {
        var local = moment.ToLocalTime();

        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("в HH:mm", Russian)
            : local.ToString("d MMMM в HH:mm", Russian);
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
            using var response = await _http.SendAsync(request, ct);
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
        catch (JsonException ex)
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
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LimitsSnapshot([], null, DateTimeOffset.UtcNow, "api.anthropic.com не ответил за 15 секунд");
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

            if (oauth.TryGetProperty("expiresAt", out var expires)
                && expires.TryGetInt64(out var milliseconds)
                && DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) <= DateTimeOffset.UtcNow)
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
            if (!value.TryGetProperty("utilization", out var utilization) || !utilization.TryGetDouble(out var raw))
                continue;

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

        double? used = value.TryGetProperty("used_credits", out var credits) && credits.TryGetDouble(out var amount)
            ? amount
            : null;

        return new ExtraUsageState(enabled, used);
    }

    /// <summary>
    /// Шкала <c>utilization</c> у Anthropic то доля (0..1), то проценты, и меняться она может
    /// без предупреждения. Всё, что больше единицы, считаем процентами.
    /// </summary>
    private static double Fraction(double raw) => raw > 1.0 ? raw / 100.0 : raw;

    private static bool IsWindow(string key) =>
        key.StartsWith("five_hour", StringComparison.Ordinal) || key.StartsWith("seven_day", StringComparison.Ordinal);

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "…";

    private static HttpClient CreateClient(GatewayOptions options)
    {
        var handler = new HttpClientHandler();

        if (options.Proxy is { Length: > 0 } proxy)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        return http;
    }
}
