namespace Postbox;

/// <summary>Отправка писем.</summary>
public interface IEmailSender
{
    /// <summary>Отправляет письмо; при временных ошибках повторяет по настройкам.</summary>
    /// <returns>Message-Id и ответ сервера.</returns>
    /// <exception cref="MailException">Письмо не ушло: причина в <see cref="MailException.Kind"/>.</exception>
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
