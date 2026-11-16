using MimeKit;

namespace Postbox;

/// <summary>Письмо к отправке.</summary>
/// <example>
/// <code>
/// var message = new EmailMessage
/// {
///     To = { "user@example.com" },
///     Subject = "Показания счётчиков",
///     TextBody = "Холодная вода: 123",
///     Attachments = { EmailAttachment.FromFile("photo.jpg") },
/// };
/// </code>
/// </example>
public sealed class EmailMessage
{
    /// <summary>Отправитель; <c>null</c> — <see cref="SmtpOptions.DefaultFrom"/>.</summary>
    public EmailAddress? From { get; set; }

    /// <summary>Кому.</summary>
    public IList<EmailAddress> To { get; } = new List<EmailAddress>();

    /// <summary>Копия.</summary>
    public IList<EmailAddress> Cc { get; } = new List<EmailAddress>();

    /// <summary>Скрытая копия.</summary>
    public IList<EmailAddress> Bcc { get; } = new List<EmailAddress>();

    /// <summary>Куда отвечать, если не отправителю.</summary>
    public IList<EmailAddress> ReplyTo { get; } = new List<EmailAddress>();

    /// <summary>Тема.</summary>
    public string? Subject { get; set; }

    /// <summary>Текст письма.</summary>
    public string? TextBody { get; set; }

    /// <summary>HTML-версия письма. Вместе с <see cref="TextBody"/> уходит как альтернативные части.</summary>
    public string? HtmlBody { get; set; }

    /// <summary>Вложения и встроенные в HTML картинки.</summary>
    public IList<EmailAttachment> Attachments { get; } = new List<EmailAttachment>();

    /// <summary>Дополнительные заголовки, например <c>X-Request-Id</c>. Служебные (From, To, Subject, Content-Type…) задаются свойствами.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Файл, приложенный к письму.</summary>
public sealed class EmailAttachment
{
    /// <summary>Создаёт вложение из байтов.</summary>
    /// <param name="fileName">Имя файла у получателя.</param>
    /// <param name="content">Содержимое.</param>
    /// <param name="contentType">MIME-тип; <c>null</c> — по расширению имени.</param>
    public EmailAttachment(string fileName, byte[] content, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);
        FileName = fileName;
        Content = content;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? MimeTypes.GetMimeType(fileName) : contentType;
    }

    /// <summary>Имя файла у получателя.</summary>
    public string FileName { get; }

    /// <summary>Содержимое.</summary>
    public byte[] Content { get; }

    /// <summary>MIME-тип, например <c>image/jpeg</c>.</summary>
    public string ContentType { get; }

    /// <summary>
    /// Идентификатор для встраивания в HTML (<c>&lt;img src="cid:logo"&gt;</c>); задан — файл встраивается, а не прикладывается.
    /// </summary>
    public string? ContentId { get; init; }

    /// <summary>Вложение из файла на диске.</summary>
    public static EmailAttachment FromFile(string path, string? contentType = null) =>
        new(Path.GetFileName(path), File.ReadAllBytes(path), contentType);

    /// <summary>Вложение из файла на диске, асинхронно.</summary>
    public static async Task<EmailAttachment> FromFileAsync(string path, string? contentType = null, CancellationToken cancellationToken = default) =>
        new(Path.GetFileName(path), await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), contentType);

    /// <summary>Картинка, встроенная в HTML по <c>cid:</c>.</summary>
    public static EmailAttachment Inline(string fileName, byte[] content, string contentId, string? contentType = null) =>
        new(fileName, content, contentType) { ContentId = contentId };
}

/// <summary>Итог отправки.</summary>
/// <param name="MessageId">Заголовок Message-Id письма (без угловых скобок): по нему письмо ищется у получателя и в ответах.</param>
/// <param name="ServerResponse">Ответ сервера на письмо, например <c>2.0.0 Ok: queued as …</c>.</param>
/// <param name="Attempts">С какой попытки ушло.</param>
public sealed record EmailSendResult(string MessageId, string ServerResponse, int Attempts)
{
    /// <summary>
    /// Адресаты, которых сервер не принял, хотя письмо остальным ушло. Если не принят никто — бросается <see cref="MailException"/>.
    /// </summary>
    public IReadOnlyList<EmailAddress> RejectedRecipients { get; init; } = [];
}
