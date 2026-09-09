namespace AgentsTracker.Channels;

/// <summary>
/// Пределы канала, под которые хост режет текст и кнопки. Значения — безопасный бюджет, а не
/// протокольный максимум: у Telegram сообщение до 4096 символов, а здесь 3800, чтобы запас
/// пережил экранирование и приписки.
/// </summary>
/// <param name="MessageLength">Сколько символов готового текста влезает в одно сообщение.</param>
/// <param name="ButtonLabelLength">Предел подписи на кнопке.</param>
/// <param name="ButtonDataBytes">Предел данных кнопки в байтах UTF-8.</param>
/// <param name="DocumentBytes">Предел размера файла-документа в байтах.</param>
/// <param name="PhotoBytes">Предел размера фото в байтах.</param>
/// <param name="CaptionLength">Предел подписи к файлу или фото в символах.</param>
/// <param name="AttachmentBytes">
/// Предел размера вложения, которое канал отдаст на скачивание. Отдельно от
/// <paramref name="DocumentBytes"/>: у Telegram отдать боту можно 50 МБ, а забрать — 20 МБ.
/// </param>
public sealed record ChannelLimits(
    int MessageLength,
    int ButtonLabelLength,
    int ButtonDataBytes,
    long DocumentBytes,
    long PhotoBytes,
    int CaptionLength,
    long AttachmentBytes);
