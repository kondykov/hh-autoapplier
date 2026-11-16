using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace Postbox.Internal;

// Переводит исключения MailKit и сети в MailException с причиной и признаком «временная»
internal static class MailErrors
{
    public static MailException Invalid(string message) => new(MailErrorKind.InvalidMessage, message);

    // null — исключение не почтовое (ошибка в коде или отмена вызывающим): пробрасывается как есть
    public static MailException? Classify(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        MailException mail => mail,
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,

        MailKit.Security.AuthenticationException e =>
            new(MailErrorKind.Authentication, $"Сервер не принял логин или пароль: {e.Message}", false, e),
        SslHandshakeException e =>
            new(MailErrorKind.Tls, $"Не удалось установить защищённое соединение: {e.Message}", false, e),

        SmtpCommandException e => Smtp(e),
        SmtpProtocolException e =>
            new(MailErrorKind.Protocol, $"SMTP-сервер ответил не по протоколу: {e.Message}", true, e),

        FolderNotFoundException e =>
            new(MailErrorKind.NotFound, $"Папка '{e.FolderName}' не найдена", false, e),
        MessageNotFoundException e =>
            new(MailErrorKind.NotFound, $"Письмо не найдено: {e.Message}", false, e),
        ImapCommandException e =>
            new(MailErrorKind.Rejected, $"IMAP-сервер отклонил команду ({e.Response}): {e.ResponseText}", false, e),
        ImapProtocolException e =>
            new(MailErrorKind.Protocol, $"IMAP-сервер ответил не по протоколу: {e.Message}", true, e),

        TimeoutException e =>
            new(MailErrorKind.Timeout, "Сервер не ответил за отведённое время", true, e),
        OperationCanceledException e =>
            new(MailErrorKind.Timeout, "Сервер не ответил за отведённое время", true, e),
        SocketException e => Socket(e, e),
        IOException { InnerException: SocketException socket } e => Socket(socket, e),
        IOException e =>
            new(MailErrorKind.Connection, $"Соединение с сервером оборвалось: {e.Message}", true, e),
        ServiceNotConnectedException e =>
            new(MailErrorKind.Connection, $"Нет соединения с сервером: {e.Message}", true, e),

        _ => null,
    };

    private static MailException Socket(SocketException socket, Exception original) => socket.SocketErrorCode switch
    {
        SocketError.TimedOut =>
            new(MailErrorKind.Timeout, "Сервер не ответил за отведённое время", true, original),
        SocketError.HostNotFound or SocketError.NoData =>
            new(MailErrorKind.Connection, $"Сервер не найден: {socket.Message}", true, original),
        _ =>
            new(MailErrorKind.Connection, $"Не удалось связаться с сервером: {socket.Message}", true, original),
    };

    private static MailException Smtp(SmtpCommandException e)
    {
        var code = (int)e.StatusCode;
        // 4xx — временный отказ (ящик занят, лимит, грейлистинг): повтор может пройти; 5xx — окончательный
        var transient = code is >= 400 and < 500;
        var what = e.ErrorCode switch
        {
            SmtpErrorCode.SenderNotAccepted => $"Сервер не принял отправителя {e.Mailbox}",
            SmtpErrorCode.RecipientNotAccepted => $"Сервер не принял адресата {e.Mailbox}",
            SmtpErrorCode.MessageNotAccepted => "Сервер не принял письмо",
            _ => "SMTP-сервер отклонил команду",
        };
        return new MailException(MailErrorKind.Rejected, $"{what}: {code} {e.Message}", transient, e);
    }
}
