using System.Globalization;

namespace AgentsTracker.Gateway.Infrastructure.Chat;

/// <summary>Единое представление токенов и времени во всех сообщениях чата.</summary>
public static class DisplayFormat
{
    extension(int count)
    {
        /// <summary>«1 вызов», «2 вызова», «5 вызовов» — формы для 1, 2–4 и остальных.</summary>
        public string Count(string one, string few, string many)
        {
            var form = count % 100 is >= 11 and <= 19
                ? many
                : (count % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
            return $"{count} {form}";
        }
    }

    extension(long count)
    {
        public string Tokens => count switch
        {
            < 1_000 => count.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => (count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
            _ => (count / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        };

        /// <summary>Размер файла: «512 Б», «3,4 КБ», «12 МБ».</summary>
        public string Bytes => count switch
        {
            < 1024 => $"{count} Б",
            < 1024 * 1024 => (count / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " КБ",
            _ => (count / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " МБ",
        };
    }

    extension(TimeSpan span)
    {
        public string Elapsed
        {
            get
            {
                if (span.TotalHours >= 1) return $"{(int)span.TotalHours} ч {span.Minutes} мин";
                if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} мин {span.Seconds} с";
                return $"{(int)span.TotalSeconds} с";
            }
        }
    }

    extension(DateTimeOffset moment)
    {
        /// <summary>«сегодня 12:41», «вчера 09:03», иначе дата с временем.</summary>
        public string Ago
        {
            get
            {
                var local = moment.ToLocalTime();
                var today = DateTimeOffset.Now.Date;

                if (local.Date == today) return "сегодня " + local.ToString("HH:mm");
                if (local.Date == today.AddDays(-1)) return "вчера " + local.ToString("HH:mm");
                return local.ToString("d MMM, HH:mm");
            }
        }
    }

    extension(string sessionId)
    {
        /// <summary>Первые восемь символов id сессии: достаточно, чтобы узнать ветку в списке.</summary>
        public string ShortId => sessionId.Length <= 8 ? sessionId : sessionId[..8];
    }
}
