using System.Text;
using MimeKit;
using Postbox.Internal;

namespace Postbox.Tests;

public class MessageBuilderTests
{
    private static readonly EmailAddress DefaultFrom = new("robot@example.com", "Робот");

    [Fact]
    public void Builds_text_html_attachments_inline_and_headers()
    {
        var message = new EmailMessage
        {
            To = { "a@example.com", new EmailAddress("b@example.com", "Бэ") },
            Cc = { "c@example.com" },
            Bcc = { "d@example.com" },
            ReplyTo = { "reply@example.com" },
            Subject = "Показания",
            TextBody = "текст",
            HtmlBody = "<p>html <img src=\"cid:logo\"></p>",
            Attachments =
            {
                new EmailAttachment("readings.csv", Encoding.UTF8.GetBytes("cold;123")),
                EmailAttachment.Inline("logo.png", [1, 2, 3], "logo"),
            },
            Headers = { ["X-Request-Id"] = "42" },
        };

        var mime = MimeMessageBuilder.Build(message, DefaultFrom);

        Assert.Equal("robot@example.com", mime.From.Mailboxes.Single().Address);
        Assert.Equal("Робот", mime.From.Mailboxes.Single().Name);
        Assert.Equal(["a@example.com", "b@example.com"], mime.To.Mailboxes.Select(m => m.Address));
        Assert.Equal("c@example.com", mime.Cc.Mailboxes.Single().Address);
        Assert.Equal("d@example.com", mime.Bcc.Mailboxes.Single().Address);
        Assert.Equal("reply@example.com", mime.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("Показания", mime.Subject);
        Assert.Equal("текст", mime.TextBody);
        Assert.Contains("cid:logo", mime.HtmlBody);
        Assert.Equal("42", mime.Headers["X-Request-Id"]);
        Assert.EndsWith("@example.com", mime.MessageId);

        var attachment = Assert.IsAssignableFrom<MimePart>(mime.Attachments.Single());
        Assert.Equal("readings.csv", attachment.FileName);
        Assert.Equal("text/csv", attachment.ContentType.MimeType);

        var inline = mime.BodyParts.OfType<MimePart>().Single(p => p.ContentId == "logo");
        Assert.Equal("image/png", inline.ContentType.MimeType);
        Assert.False(inline.IsAttachment);
    }

    [Fact]
    public void Message_from_overrides_default()
    {
        var mime = MimeMessageBuilder.Build(new EmailMessage { From = "me@example.org", To = { "a@example.com" } }, DefaultFrom);
        Assert.Equal("me@example.org", mime.From.Mailboxes.Single().Address);
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void Invalid_message_throws_InvalidMessage(EmailMessage message, string expected)
    {
        var error = Assert.Throws<MailException>(() => MimeMessageBuilder.Build(message, DefaultFrom));
        Assert.Equal(MailErrorKind.InvalidMessage, error.Kind);
        Assert.False(error.IsTransient);
        Assert.Contains(expected, error.Message);
    }

    public static TheoryData<EmailMessage, string> InvalidMessages() => new()
    {
        { new EmailMessage { Subject = "без адресатов" }, "нет адресатов" },
        { new EmailMessage { To = { new EmailAddress("not-an-address") } }, "not-an-address" },
        { new EmailMessage { To = { "a@example.com" }, Headers = { ["Subject"] = "x" } }, "Subject" },
        { new EmailMessage { To = { "a@example.com" }, Headers = { ["Bad Name"] = "x" } }, "Bad Name" },
        {
            new EmailMessage { To = { "a@example.com" }, TextBody = "t", Attachments = { EmailAttachment.Inline("a.png", [1], "a") } },
            "HtmlBody"
        },
    };

    [Fact]
    public void Without_any_sender_throws()
    {
        var error = Assert.Throws<MailException>(() => MimeMessageBuilder.Build(new EmailMessage { To = { "a@example.com" } }, null));
        Assert.Contains("отправитель", error.Message);
    }

    [Fact]
    public void Attachment_type_is_guessed_from_name()
    {
        Assert.Equal("application/pdf", new EmailAttachment("report.pdf", [1]).ContentType);
        Assert.Equal("text/plain", new EmailAttachment("a.txt", [1], "text/plain").ContentType);
    }
}
