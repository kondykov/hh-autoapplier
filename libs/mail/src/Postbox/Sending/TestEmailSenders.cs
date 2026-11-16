using System.Collections.Concurrent;
using Postbox.Internal;

namespace Postbox;

/// <summary>
/// Отправка в память — для тестов приложений: письма проверяются так же, как в SMTP, и складываются в <see cref="Sent"/>.
/// </summary>
public sealed class InMemoryEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();
    private readonly EmailAddress _defaultFrom;

    /// <summary>Создаёт отправитель; <paramref name="defaultFrom"/> — для писем без From.</summary>
    public InMemoryEmailSender(EmailAddress? defaultFrom = null) =>
        _defaultFrom = defaultFrom ?? new EmailAddress("postbox@localhost");

    /// <summary>Отправленные письма в порядке отправки.</summary>
    public IReadOnlyList<EmailMessage> Sent => _sent.ToArray();

    /// <summary>Забыть отправленное.</summary>
    public void Clear() => _sent.Clear();

    /// <inheritdoc />
    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mime = MimeMessageBuilder.Build(message, _defaultFrom);
        _sent.Enqueue(message);
        return Task.FromResult(new EmailSendResult(mime.MessageId ?? string.Empty, "250 accepted in memory", 1));
    }
}

/// <summary>Отправка в никуда: письмо проверяется и выбрасывается. Для окружений, где почта выключена.</summary>
public sealed class NullEmailSender : IEmailSender
{
    /// <inheritdoc />
    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mime = MimeMessageBuilder.Build(message, new EmailAddress("postbox@localhost"));
        return Task.FromResult(new EmailSendResult(mime.MessageId ?? string.Empty, "250 discarded", 1));
    }
}
