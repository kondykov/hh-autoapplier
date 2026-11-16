using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Postbox.Internal;

namespace Postbox;

/// <summary>
/// Отправка по SMTP через MailKit. На каждое письмо — своё соединение: подходит для уведомлений и отчётов,
/// а не для рассылок на тысячи адресов.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptionsMonitor<SmtpOptions>? _monitor;
    private readonly SmtpOptions? _fixed;
    private readonly ILogger _logger;

    /// <summary>Конструктор для DI: настройки из <see cref="IOptionsMonitor{TOptions}"/> подхватывают изменения конфигурации.</summary>
    public SmtpEmailSender(IOptionsMonitor<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _monitor = options;
        _logger = logger;
    }

    /// <summary>Конструктор без DI.</summary>
    /// <exception cref="OptionsValidationException">Настройки неверны.</exception>
    public SmtpEmailSender(SmtpOptions options, ILogger? logger = null)
    {
        var result = new MailServerOptionsValidator<SmtpOptions>().Validate(null, options);
        if (result.Failed)
            throw new OptionsValidationException(nameof(SmtpOptions), typeof(SmtpOptions), result.Failures);
        _fixed = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var options = _fixed ?? _monitor!.CurrentValue;
        var defaultFrom = string.IsNullOrWhiteSpace(options.DefaultFrom) ? null : EmailAddress.Parse(options.DefaultFrom);
        var mime = MimeMessageBuilder.Build(message, defaultFrom);

        var (response, attempts) = await MailRetry.RunAsync(async (attempt, token) =>
        {
            using var client = new SmtpClient();
            try
            {
                await MailConnection.OpenAsync(client, options, token).ConfigureAwait(false);
                var sent = await client.SendAsync(mime, token).ConfigureAwait(false);
                return (sent, attempt);
            }
            finally
            {
                await MailConnection.CloseAsync(client).ConfigureAwait(false);
            }
        }, options, _logger, "Отправка письма", cancellationToken).ConfigureAwait(false);

        var rejected = response.RejectedRecipients.Select(EmailAddress.From).ToList();
        if (rejected.Count > 0)
            _logger.LogWarning("Письмо {MessageId}: сервер не принял адресатов {Rejected}", mime.MessageId, string.Join(", ", rejected));
        _logger.LogInformation("Письмо {MessageId} отправлено через {Host}: принято адресатов {Accepted}, попытка {Attempt}",
            mime.MessageId, options.Host, response.AcceptedRecipients.Count, attempts);
        return new EmailSendResult(mime.MessageId ?? string.Empty, response.ResponseText, attempts) { RejectedRecipients = rejected };
    }
}
