using MailKit;
using MailKit.Security;

namespace Postbox.Internal;

internal static class MailConnection
{
    public static SecureSocketOptions ToSocketOptions(MailSecurity security, int port) => security switch
    {
        MailSecurity.None => SecureSocketOptions.None,
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        MailSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => port is 465 or 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
    };

    public static async Task OpenAsync(MailService client, MailServerOptions options, CancellationToken cancellationToken)
    {
        client.Timeout = options.TimeoutSeconds * 1000;
        await client.ConnectAsync(options.Host, options.Port, ToSocketOptions(options.Security, options.Port), cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(options.UserName))
            await client.AuthenticateAsync(options.UserName, options.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    // Ошибка при вежливом закрытии не должна превращать удачную операцию в неудачную (и запускать повтор)
    public static async Task CloseAsync(MailService client)
    {
        if (!client.IsConnected)
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // соединение всё равно закрывается при Dispose
        }
    }
}
