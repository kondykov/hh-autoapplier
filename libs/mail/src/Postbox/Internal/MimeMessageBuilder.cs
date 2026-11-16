using MimeKit;
using MimeKit.Utils;

namespace Postbox.Internal;

// EmailMessage → MimeMessage; здесь же проверка письма: ошибки — MailException(InvalidMessage)
internal static class MimeMessageBuilder
{
    // Эти заголовки задаются свойствами письма или самим MIME: через Headers их не подменить
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "From", "Sender", "To", "Cc", "Bcc", "Reply-To", "Subject", "Date", "Message-Id",
        "MIME-Version", "Content-Type", "Content-Transfer-Encoding", "Content-Disposition",
    };

    public static MimeMessage Build(EmailMessage message, EmailAddress? defaultFrom)
    {
        ArgumentNullException.ThrowIfNull(message);

        var from = message.From ?? defaultFrom
            ?? throw MailErrors.Invalid("Не задан отправитель: укажите From у письма или SmtpOptions.DefaultFrom");
        if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
            throw MailErrors.Invalid("У письма нет адресатов: заполните To, Cc или Bcc");

        var mime = new MimeMessage();
        mime.From.Add(Mailbox(from, "отправитель"));
        AddAll(mime.To, message.To, "адресат");
        AddAll(mime.Cc, message.Cc, "адресат копии");
        AddAll(mime.Bcc, message.Bcc, "адресат скрытой копии");
        AddAll(mime.ReplyTo, message.ReplyTo, "адрес для ответа");
        mime.Subject = message.Subject ?? string.Empty;
        mime.Date = DateTimeOffset.Now;
        mime.MessageId = MimeUtils.GenerateMessageId(from.Address[(from.Address.IndexOf('@') + 1)..]);

        foreach (var (name, value) in message.Headers)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => c is ':' || char.IsWhiteSpace(c) || char.IsControl(c)))
                throw MailErrors.Invalid($"Недопустимое имя заголовка: '{name}'");
            if (Reserved.Contains(name))
                throw MailErrors.Invalid($"Заголовок {name} задаётся свойством письма, а не через Headers");
            mime.Headers.Add(name, value ?? string.Empty);
        }

        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var attachment in message.Attachments)
        {
            if (!ContentType.TryParse(attachment.ContentType, out var contentType))
                throw MailErrors.Invalid($"Недопустимый тип вложения {attachment.FileName}: '{attachment.ContentType}'");
            if (attachment.ContentId is { Length: > 0 } contentId)
            {
                if (message.HtmlBody is null)
                    throw MailErrors.Invalid($"Встроенная картинка {attachment.FileName} без HtmlBody: её некуда встроить");
                var part = body.LinkedResources.Add(attachment.FileName, attachment.Content, contentType);
                part.ContentId = contentId;
            }
            else
            {
                body.Attachments.Add(attachment.FileName, attachment.Content, contentType);
            }
        }
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static void AddAll(InternetAddressList target, IEnumerable<EmailAddress> addresses, string role)
    {
        foreach (var address in addresses)
            target.Add(Mailbox(address, role));
    }

    private static MailboxAddress Mailbox(EmailAddress? address, string role)
    {
        if (address is null || !EmailAddress.IsAddress(address.Address))
            throw MailErrors.Invalid($"Неверный адрес ({role}): '{address?.Address}'");
        return address.ToMailbox();
    }
}
