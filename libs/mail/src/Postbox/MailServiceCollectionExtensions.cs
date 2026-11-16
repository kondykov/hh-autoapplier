using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Postbox;
using Postbox.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Регистрация почты в DI. Настройки проверяются при старте хоста.</summary>
public static class MailServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует то, что настроено в секции: <c>Smtp</c> — <see cref="IEmailSender"/>, <c>Imap</c> — <see cref="IMailboxReader"/>.
    /// </summary>
    /// <example><c>services.AddMail(configuration.GetSection("Mail"));</c></example>
    /// <exception cref="InvalidOperationException">В секции нет ни <c>Smtp</c>, ни <c>Imap</c>.</exception>
    public static IServiceCollection AddMail(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var smtp = section.GetSection("Smtp");
        var imap = section.GetSection("Imap");
        if (!smtp.Exists() && !imap.Exists())
            throw new InvalidOperationException($"В секции настроек '{(section as IConfigurationSection)?.Path}' нет ни Smtp, ни Imap");
        if (smtp.Exists())
            services.AddEmailSender(smtp);
        if (imap.Exists())
            services.AddMailboxReader(imap);
        return services;
    }

    /// <summary>Регистрирует отправку по SMTP с настройками из секции (поля <see cref="SmtpOptions"/>).</summary>
    public static IServiceCollection AddEmailSender(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<SmtpOptions>().Bind(section);
        return services.AddSmtpCore();
    }

    /// <summary>Регистрирует отправку по SMTP с настройками из кода.</summary>
    public static IServiceCollection AddEmailSender(this IServiceCollection services, Action<SmtpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<SmtpOptions>().Configure(configure);
        return services.AddSmtpCore();
    }

    /// <summary>Регистрирует чтение по IMAP с настройками из секции (поля <see cref="ImapOptions"/>).</summary>
    public static IServiceCollection AddMailboxReader(this IServiceCollection services, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        services.AddOptions<ImapOptions>().Bind(section);
        return services.AddImapCore();
    }

    /// <summary>Регистрирует чтение по IMAP с настройками из кода.</summary>
    public static IServiceCollection AddMailboxReader(this IServiceCollection services, Action<ImapOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<ImapOptions>().Configure(configure);
        return services.AddImapCore();
    }

    private static IServiceCollection AddSmtpCore(this IServiceCollection services)
    {
        services.AddOptions<SmtpOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SmtpOptions>, MailServerOptionsValidator<SmtpOptions>>());
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }

    private static IServiceCollection AddImapCore(this IServiceCollection services)
    {
        services.AddOptions<ImapOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ImapOptions>, MailServerOptionsValidator<ImapOptions>>());
        services.TryAddSingleton<IMailboxReader, ImapMailboxReader>();
        return services;
    }
}
