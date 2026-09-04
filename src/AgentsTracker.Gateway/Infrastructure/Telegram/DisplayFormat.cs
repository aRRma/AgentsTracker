using System.Globalization;

namespace AgentsTracker.Gateway.Infrastructure.Telegram;

/// <summary>Единое представление сумм, токенов и времени во всех сообщениях чата.</summary>
public static class DisplayFormat
{
    extension(decimal value)
    {
        /// <summary>Суммы всегда в инвариантной культуре: иначе на русской локали получается «$0,08».</summary>
        public string Money => value switch
        {
            0m => "$0",
            > 0m and < 0.01m => "<$0.01",
            _ => "$" + value.ToString("0.00", CultureInfo.InvariantCulture),
        };
    }

    extension(long count)
    {
        public string Tokens => count switch
        {
            < 1_000 => count.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => (count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
            _ => (count / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
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
