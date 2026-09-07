namespace AgentsTracker.Channels;

/// <summary>
/// Пределы канала, под которые хост режет текст и кнопки. Значения — безопасный бюджет, а не
/// протокольный максимум: у Telegram сообщение до 4096 символов, а здесь 3800, чтобы запас
/// пережил экранирование и приписки.
/// </summary>
/// <param name="MessageLength">Сколько символов готового текста влезает в одно сообщение.</param>
/// <param name="ButtonLabelLength">Предел подписи на кнопке.</param>
/// <param name="ButtonDataBytes">Предел данных кнопки в байтах UTF-8.</param>
public sealed record ChannelLimits(int MessageLength, int ButtonLabelLength, int ButtonDataBytes);
