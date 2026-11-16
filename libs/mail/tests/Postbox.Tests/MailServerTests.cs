using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Postbox.Tests;

// Настоящий SMTP+IMAP: GreenMail в Docker. Пользователь demo/secret, ящик demo@example.com
public sealed class GreenMailFixture : IAsyncLifetime
{
    public const string User = "demo";
    public const string Password = "secret";
    public const string Address = "demo@example.com";

    private const int SmtpPort = 3025;
    private const int ImapPort = 3143;

    private readonly IContainer _container = new ContainerBuilder("greenmail/standalone:2.1.5")
        .WithEnvironment("GREENMAIL_OPTS",
            $"-Dgreenmail.setup.test.smtp -Dgreenmail.setup.test.imap -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users={User}:{Password}@example.com")
        .WithPortBinding(SmtpPort, true)
        .WithPortBinding(ImapPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(SmtpPort).UntilInternalTcpPortIsAvailable(ImapPort))
        .Build();

    public SmtpOptions Smtp(Action<SmtpOptions>? change = null)
    {
        var options = new SmtpOptions
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(SmtpPort),
            Security = MailSecurity.None,
            UserName = User,
            Password = Password,
            DefaultFrom = $"Тест <{Address}>",
            RetryDelayMilliseconds = 0,
            TimeoutSeconds = 10,
        };
        change?.Invoke(options);
        return options;
    }

    public ImapOptions Imap(Action<ImapOptions>? change = null)
    {
        var options = new ImapOptions
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(ImapPort),
            Security = MailSecurity.None,
            UserName = User,
            Password = Password,
            RetryDelayMilliseconds = 0,
            TimeoutSeconds = 10,
        };
        change?.Invoke(options);
        return options;
    }

    // Библиотека папки не создаёт — для теста переноса создаём напрямую
    public async Task CreateFolderAsync(string name)
    {
        using var client = new ImapClient();
        var imap = Imap();
        await client.ConnectAsync(imap.Host, imap.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync(User, Password);
        var root = client.GetFolder(client.PersonalNamespaces[0]);
        if (!(await root.GetSubfoldersAsync()).Any(f => f.Name == name))
            await root.CreateAsync(name, true);
        await client.DisconnectAsync(true);
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public sealed class MailServerTests(GreenMailFixture server) : IClassFixture<GreenMailFixture>
{
    private readonly SmtpEmailSender _sender = new(server.Smtp());
    private readonly ImapMailboxReader _reader = new(server.Imap());

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private async Task<EmailSummary> SendAndFindAsync(string subject, Action<EmailMessage>? fill = null)
    {
        var message = new EmailMessage { To = { GreenMailFixture.Address }, Subject = subject, TextBody = "текст" };
        fill?.Invoke(message);
        await _sender.SendAsync(message);

        // доставка в GreenMail мгновенная, но асинхронная
        for (var i = 0; i < 50; i++)
        {
            var found = await _reader.SearchAsync(new MailSearch { Subject = subject });
            if (found.Count > 0)
                return Assert.Single(found);
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Письмо «{subject}» не дошло");
    }

    [Fact]
    public async Task Sent_message_arrives_whole()
    {
        var subject = Unique("Показания счётчиков");
        var csv = Encoding.UTF8.GetBytes("холодная;123\nгорячая;45");

        var summary = await SendAndFindAsync(subject, m =>
        {
            m.HtmlBody = "<p>Показания <img src=\"cid:logo\"></p>";
            m.Cc.Add("copy@example.com");
            m.ReplyTo.Add("reply@example.com");
            m.Headers["X-Readings-Period"] = "2026-11";
            m.Attachments.Add(new EmailAttachment("показания.csv", csv));
            m.Attachments.Add(EmailAttachment.Inline("logo.png", [137, 80, 78, 71], "logo"));
        });

        Assert.False(summary.IsRead);
        Assert.True(summary.HasAttachments);
        Assert.Equal(GreenMailFixture.Address, summary.From.Single().Address);
        Assert.Equal("Тест", summary.From.Single().Name);

        var email = await _reader.GetAsync(summary.Id);
        Assert.Equal(subject, email.Subject);
        Assert.Equal("текст", email.TextBody?.Trim());
        Assert.Contains("cid:logo", email.HtmlBody);
        Assert.Equal("copy@example.com", email.Cc.Single().Address);
        Assert.Equal("reply@example.com", email.ReplyTo.Single().Address);
        Assert.Contains(email.Headers, h => h.Key == "X-Readings-Period" && h.Value == "2026-11");
        Assert.Equal(csv, email.Attachments.Single(a => a.FileName == "показания.csv").Content);
        Assert.True(email.Attachments.Single(a => a.ContentId == "logo").IsInline);

        // чтение письма не помечает его прочитанным
        Assert.False((await _reader.GetAsync(summary.Id)).IsRead);
    }

    [Fact]
    public async Task Read_flag_can_be_set_and_cleared()
    {
        var subject = Unique("Флаг");
        var summary = await SendAndFindAsync(subject);

        await _reader.SetReadAsync(summary.Id);
        Assert.Empty(await _reader.SearchAsync(new MailSearch { Subject = subject, UnreadOnly = true }));
        Assert.True((await _reader.GetAsync(summary.Id)).IsRead);

        await _reader.SetReadAsync(summary.Id, isRead: false);
        Assert.Single(await _reader.SearchAsync(new MailSearch { Subject = subject, UnreadOnly = true }));
    }

    [Fact]
    public async Task Message_can_be_moved_and_deleted()
    {
        await server.CreateFolderAsync("Processed");
        var subject = Unique("Перенос");
        var summary = await SendAndFindAsync(subject);

        await _reader.MoveAsync(summary.Id, "Processed");
        Assert.Empty(await _reader.SearchAsync(new MailSearch { Subject = subject }));
        var moved = Assert.Single(await _reader.SearchAsync(new MailSearch { Folder = "Processed", Subject = subject }));

        var gone = await Assert.ThrowsAsync<MailException>(() => _reader.GetAsync(summary.Id));
        Assert.Equal(MailErrorKind.NotFound, gone.Kind);

        await _reader.DeleteAsync(moved.Id);
        Assert.Empty(await _reader.SearchAsync(new MailSearch { Folder = "Processed", Subject = subject }));

        var folders = await _reader.GetFoldersAsync();
        Assert.Contains(folders, f => f.Role == MailFolderRole.Inbox);
        Assert.Contains(folders, f => f.FullName == "Processed");
    }

    [Fact]
    public async Task Search_filters_by_time_and_respects_limit()
    {
        var prefix = Unique("Лимит");
        for (var i = 0; i < 3; i++)
            await SendAndFindAsync($"{prefix} {i}");

        var two = await _reader.SearchAsync(new MailSearch { Subject = prefix, Limit = 2 });
        Assert.Equal(2, two.Count);
        Assert.DoesNotContain(two, s => s.Subject.EndsWith(" 0"));

        Assert.Empty(await _reader.SearchAsync(new MailSearch { Subject = prefix, Since = DateTimeOffset.Now.AddHours(1) }));
        Assert.Equal(3, (await _reader.SearchAsync(new MailSearch { Subject = prefix, Since = DateTimeOffset.Now.AddHours(-1) })).Count);
    }

    [Fact]
    public async Task Missing_folder_is_not_found()
    {
        var error = await Assert.ThrowsAsync<MailException>(() => _reader.SearchAsync(new MailSearch { Folder = "Нет такой" }));
        Assert.Equal(MailErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task Wrong_password_is_authentication_error_without_retries()
    {
        var reader = new ImapMailboxReader(server.Imap(o => o.Password = "wrong"));
        var imapError = await Assert.ThrowsAsync<MailException>(() => reader.SearchAsync());
        Assert.Equal(MailErrorKind.Authentication, imapError.Kind);
        Assert.Equal(1, imapError.Attempts);

        var sender = new SmtpEmailSender(server.Smtp(o => o.Password = "wrong"));
        var smtpError = await Assert.ThrowsAsync<MailException>(() =>
            sender.SendAsync(new EmailMessage { To = { GreenMailFixture.Address }, Subject = "x" }));
        Assert.Equal(MailErrorKind.Authentication, smtpError.Kind);
    }

    [Fact]
    public async Task Unreachable_server_is_connection_error_after_all_attempts()
    {
        var sender = new SmtpEmailSender(new SmtpOptions
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Security = MailSecurity.None,
            MaxAttempts = 2,
            RetryDelayMilliseconds = 0,
            DefaultFrom = GreenMailFixture.Address,
        });

        var error = await Assert.ThrowsAsync<MailException>(() =>
            sender.SendAsync(new EmailMessage { To = { GreenMailFixture.Address }, Subject = "x" }));
        Assert.Equal(MailErrorKind.Connection, error.Kind);
        Assert.True(error.IsTransient);
        Assert.Equal(2, error.Attempts);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
