using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Postbox.Tests;

public class RegistrationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void AddMail_registers_what_is_configured()
    {
        var config = Config(new()
        {
            ["Mail:Smtp:Host"] = "smtp.example.com",
            ["Mail:Smtp:Port"] = "465",
            ["Mail:Smtp:Security"] = "SslOnConnect",
            ["Mail:Smtp:DefaultFrom"] = "Робот <robot@example.com>",
            ["Mail:Imap:Host"] = "imap.example.com",
        });
        using var provider = new ServiceCollection().AddLogging().AddMail(config.GetSection("Mail")).BuildServiceProvider();

        Assert.IsType<SmtpEmailSender>(provider.GetRequiredService<IEmailSender>());
        Assert.IsType<ImapMailboxReader>(provider.GetRequiredService<IMailboxReader>());
        var smtp = provider.GetRequiredService<IOptions<SmtpOptions>>().Value;
        Assert.Equal(465, smtp.Port);
        Assert.Equal(MailSecurity.SslOnConnect, smtp.Security);
        Assert.Equal(993, provider.GetRequiredService<IOptions<ImapOptions>>().Value.Port);
    }

    [Fact]
    public void AddMail_with_only_smtp_does_not_register_reader()
    {
        var config = Config(new() { ["Mail:Smtp:Host"] = "smtp.example.com" });
        using var provider = new ServiceCollection().AddLogging().AddMail(config.GetSection("Mail")).BuildServiceProvider();
        Assert.NotNull(provider.GetService<IEmailSender>());
        Assert.Null(provider.GetService<IMailboxReader>());
    }

    [Fact]
    public void AddMail_without_sections_fails_fast()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMail(Config(new()).GetSection("Mail")));
        Assert.Contains("Mail", error.Message);
    }

    [Fact]
    public void Invalid_settings_are_reported_together()
    {
        using var provider = new ServiceCollection().AddLogging()
            .AddEmailSender(o =>
            {
                o.Port = 0;
                o.UserName = "user";
                o.DefaultFrom = "garbage";
                o.MaxAttempts = 0;
            })
            .BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);
        var text = string.Join("\n", error.Failures);
        Assert.Contains("Host", text);
        Assert.Contains("Port", text);
        Assert.Contains("логин и пароль", text);
        Assert.Contains("DefaultFrom", text);
        Assert.Contains("MaxAttempts", text);
    }

    [Fact]
    public void Manual_construction_validates_too()
    {
        Assert.Throws<OptionsValidationException>(() => new SmtpEmailSender(new SmtpOptions()));
        Assert.Throws<OptionsValidationException>(() => new ImapMailboxReader(new ImapOptions { Host = "imap", DefaultFolder = " " }));
    }

    [Fact]
    public async Task InMemory_sender_validates_and_keeps_messages()
    {
        var sender = new InMemoryEmailSender();
        var result = await sender.SendAsync(new EmailMessage { To = { "a@example.com" }, Subject = "s" });

        Assert.Equal("s", sender.Sent.Single().Subject);
        Assert.EndsWith("@localhost", result.MessageId);
        await Assert.ThrowsAsync<MailException>(() => sender.SendAsync(new EmailMessage()));
        Assert.Single(sender.Sent);

        sender.Clear();
        Assert.Empty(sender.Sent);
    }
}
