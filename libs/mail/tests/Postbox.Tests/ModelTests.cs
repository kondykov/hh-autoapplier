using MimeKit;

namespace Postbox.Tests;

public class ModelTests
{
    [Theory]
    [InlineData("user@example.com", "user@example.com", null)]
    [InlineData("Служба <help@example.com>", "help@example.com", "Служба")]
    [InlineData("\"Иванов, Иван\" <ivan@example.com>", "ivan@example.com", "Иванов, Иван")]
    public void EmailAddress_parses(string value, string address, string? name)
    {
        var parsed = EmailAddress.Parse(value);
        Assert.Equal(address, parsed.Address);
        Assert.Equal(name, parsed.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("user@")]
    [InlineData("@example.com")]
    public void EmailAddress_rejects_garbage(string value)
    {
        Assert.False(EmailAddress.TryParse(value, out _));
        Assert.Throws<FormatException>(() => EmailAddress.Parse(value));
    }

    [Fact]
    public void EmailId_round_trips_folder_with_colons()
    {
        var id = new EmailId("Архив:2026", 7, 12345);
        Assert.Equal("7:12345:Архив:2026", id.ToString());
        Assert.Equal(id, EmailId.Parse(id.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1:2")]
    [InlineData("x:2:INBOX")]
    [InlineData("1:2:")]
    public void EmailId_rejects_garbage(string value) => Assert.False(EmailId.TryParse(value, out _));

    [Fact]
    public void Received_email_collects_attachments_inline_images_and_attached_messages()
    {
        var inner = new MimeMessage { Subject = "вложенное" };
        inner.From.Add(new MailboxAddress("", "x@example.com"));
        inner.Body = new TextPart("plain") { Text = "внутри" };

        var builder = new BodyBuilder { TextBody = "текст", HtmlBody = "<img src=\"cid:pic\">" };
        builder.Attachments.Add("data.csv", "a;b"u8.ToArray(), ContentType.Parse("text/csv"));
        builder.LinkedResources.Add("pic.png", new byte[] { 9, 9 }, ContentType.Parse("image/png")).ContentId = "pic";
        builder.Attachments.Add(new MessagePart { Message = inner });

        var message = new MimeMessage { Subject = "тема", Body = builder.ToMessageBody() };
        message.From.Add(new MailboxAddress("Отправитель", "from@example.com"));
        message.To.Add(new MailboxAddress("", "to@example.com"));

        var email = ImapMailboxReader.ToReceived(new EmailId("INBOX", 1, 1), message, null);

        Assert.Equal("тема", email.Subject);
        Assert.Equal("текст", email.TextBody);
        Assert.Equal("Отправитель", email.From.Single().Name);
        Assert.Equal(3, email.Attachments.Count);

        var csv = email.Attachments.Single(a => a.FileName == "data.csv");
        Assert.Equal("a;b"u8.ToArray(), csv.Content);
        Assert.False(csv.IsInline);

        var picture = email.Attachments.Single(a => a.ContentId == "pic");
        Assert.True(picture.IsInline);
        Assert.Equal(new byte[] { 9, 9 }, picture.Content);

        var attached = email.Attachments.Single(a => a.ContentType == "message/rfc822");
        Assert.Contains("вложенное", MimeMessage.Load(new MemoryStream(attached.Content)).Subject);
    }
}
