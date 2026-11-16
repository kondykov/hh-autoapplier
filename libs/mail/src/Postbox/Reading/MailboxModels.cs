using System.Globalization;

namespace Postbox;

/// <summary>
/// Адрес письма в ящике: папка, её UIDVALIDITY и UID письма. Действует, пока письмо лежит в этой папке.
/// </summary>
/// <remarks>Строковый вид — <c>UIDVALIDITY:UID:Папка</c>, обратно — <see cref="Parse"/>.</remarks>
public readonly record struct EmailId(string Folder, uint UidValidity, uint Uid)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{UidValidity}:{Uid}:{Folder}");

    /// <summary>Разбирает строку из <see cref="ToString"/>.</summary>
    /// <exception cref="FormatException">Строка не похожа на адрес письма.</exception>
    public static EmailId Parse(string value) =>
        TryParse(value, out var id) ? id : throw new FormatException($"Не удалось разобрать адрес письма: '{value}'");

    /// <summary>Пробует разобрать строку из <see cref="ToString"/>.</summary>
    public static bool TryParse(string? value, out EmailId id)
    {
        id = default;
        var parts = value?.Split(':', 3);
        if (parts is not { Length: 3 } || parts[2].Length == 0
            || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var validity)
            || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
            return false;
        id = new EmailId(parts[2], validity, uid);
        return true;
    }
}

/// <summary>Назначение папки, если сервер его сообщает.</summary>
public enum MailFolderRole
{
    /// <summary>Обычная папка.</summary>
    Normal,
    /// <summary>Входящие.</summary>
    Inbox,
    /// <summary>Отправленные.</summary>
    Sent,
    /// <summary>Черновики.</summary>
    Drafts,
    /// <summary>Корзина.</summary>
    Trash,
    /// <summary>Спам.</summary>
    Junk,
    /// <summary>Архив.</summary>
    Archive,
}

/// <summary>Папка ящика.</summary>
/// <param name="FullName">Полное имя — его передавать в <see cref="MailSearch.Folder"/> и <see cref="IMailboxReader.MoveAsync"/>.</param>
/// <param name="Name">Короткое имя для показа.</param>
/// <param name="Role">Назначение.</param>
/// <param name="Count">Писем всего.</param>
/// <param name="Unread">Непрочитанных.</param>
public sealed record MailFolderInfo(string FullName, string Name, MailFolderRole Role, int Count, int Unread);

/// <summary>Условия поиска писем. Всё заданное объединяется через «И».</summary>
public sealed class MailSearch
{
    /// <summary>Папка; <c>null</c> — <see cref="ImapOptions.DefaultFolder"/>.</summary>
    public string? Folder { get; set; }

    /// <summary>Только непрочитанные.</summary>
    public bool UnreadOnly { get; set; }

    /// <summary>Получены не раньше.</summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>Получены раньше.</summary>
    public DateTimeOffset? Before { get; set; }

    /// <summary>Отправитель содержит строку (адрес или имя).</summary>
    public string? From { get; set; }

    /// <summary>Тема содержит строку.</summary>
    public string? Subject { get; set; }

    /// <summary>Заголовки или текст содержат строку.</summary>
    public string? Text { get; set; }

    /// <summary>Сколько последних писем вернуть, не больше.</summary>
    public int Limit { get; set; } = 50;
}

/// <summary>Письмо в списке: заголовки без тела.</summary>
public sealed record EmailSummary(
    EmailId Id,
    string Subject,
    IReadOnlyList<EmailAddress> From,
    IReadOnlyList<EmailAddress> To,
    DateTimeOffset Date,
    bool IsRead,
    bool HasAttachments,
    long? Size);

/// <summary>Письмо целиком.</summary>
public sealed record ReceivedEmail(
    EmailId Id,
    string? MessageId,
    string Subject,
    IReadOnlyList<EmailAddress> From,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    IReadOnlyList<EmailAddress> ReplyTo,
    DateTimeOffset Date,
    bool IsRead,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<ReceivedAttachment> Attachments,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>Вложение полученного письма.</summary>
/// <param name="FileName">Имя файла (у безымянных — придуманное по типу).</param>
/// <param name="ContentType">MIME-тип.</param>
/// <param name="Content">Содержимое, уже раскодированное.</param>
/// <param name="ContentId">Content-Id встроенной картинки.</param>
/// <param name="IsInline">Встроено в HTML, а не приложено.</param>
public sealed record ReceivedAttachment(string FileName, string ContentType, byte[] Content, string? ContentId, bool IsInline);
