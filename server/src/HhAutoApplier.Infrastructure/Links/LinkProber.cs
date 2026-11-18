using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using HhAutoApplier.Domain.Links;

namespace HhAutoApplier.Infrastructure.Links;

// Проверка направления без запросов к API (#132): подключение (через прокси — CONNECT host:443) и TLS-рукопожатие с сервисом.
// Ключи и токены не нужны и не передаются; сертификат сервиса проверяется, как у HTTP-клиента этого сервиса: своя проверка
// направления (LinkTarget.Certificates, ГигаЧат — #138) или системная. certificates — подмена проверки для всех направлений в тестах
public class LinkProber(TimeSpan timeout, RemoteCertificateValidationCallback? certificates = null)
{
    // Ответ прокси на CONNECT — заголовки, тело не ждём
    private const int MaxProxyResponse = 8 * 1024;

    // Сам прокси: порт принимает подключения
    public async Task<LinkProbe> ProbeProxyAsync(Uri proxy, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port, deadline.Token);
            return new LinkProbe(LinkProbe.Proxy, false, LinkState.Ok);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LinkProbe(LinkProbe.Proxy, false, LinkState.ProxyUnreachable, $"порт не ответил за {Seconds} с");
        }
        catch (SocketException e)
        {
            return new LinkProbe(LinkProbe.Proxy, false, LinkState.ProxyUnreachable, $"порт не отвечает: {e.SocketErrorCode}");
        }
    }

    public async Task<LinkProbe> ProbeAsync(LinkTarget target, CancellationToken ct)
    {
        var via = target.Proxy is not null;
        LinkProbe Result(LinkState state, string? detail = null) => new(target.Link, via, state, detail);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(target.Proxy?.Host ?? target.Host, target.Proxy?.Port ?? target.Port, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return via ? Result(LinkState.ProxyUnreachable, $"прокси не ответил за {Seconds} с") : Result(LinkState.Timeout, $"нет ответа за {Seconds} с");
        }
        catch (SocketException e)
        {
            return via ? Result(LinkState.ProxyUnreachable, $"прокси: {e.SocketErrorCode}") : Result(LinkState.Error, $"нет подключения: {e.SocketErrorCode}");
        }

        var stream = tcp.GetStream();
        try
        {
            if (target.Proxy is { } proxy)
            {
                var status = await ConnectAsync(stream, proxy, target, deadline.Token);
                switch (status)
                {
                    case 200:
                        break;
                    case 502:
                        return Result(LinkState.BadGateway, "прокси: 502 Bad Gateway");
                    case 504:
                        return Result(LinkState.Timeout, "прокси: 504 Gateway Timeout");
                    default:
                        return Result(LinkState.Error, $"прокси ответил {status}");
                }
            }

            await using var tls = new SslStream(stream, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = target.Host,
                RemoteCertificateValidationCallback = certificates ?? target.Certificates,
            }, deadline.Token);
            return Result(LinkState.Ok);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(LinkState.Timeout, $"нет ответа за {Seconds} с");
        }
        catch (AuthenticationException e)
        {
            return Result(LinkState.Error, $"TLS: {e.Message}");
        }
        catch (IOException e)
        {
            return Result(LinkState.Error, $"соединение оборвалось: {e.Message}");
        }
    }

    private string Seconds => timeout.TotalSeconds.ToString("0.#");

    // CONNECT host:port; код ответа прокси. Учётные данные из HTTPS_PROXY — как у HttpClient, в итог проверки не попадают
    private static async Task<int> ConnectAsync(NetworkStream stream, Uri proxy, LinkTarget target, CancellationToken ct)
    {
        var request = new StringBuilder($"CONNECT {target.Host}:{target.Port} HTTP/1.1\r\nHost: {target.Host}:{target.Port}\r\n");
        if (!string.IsNullOrEmpty(proxy.UserInfo))
            request.Append($"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(proxy.UserInfo)))}\r\n");
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);

        // Читаем по байту до пустой строки: после 200 следом пойдёт TLS, лишнего забирать нельзя
        var head = new List<byte>(256);
        var one = new byte[1];
        while (head.Count < MaxProxyResponse)
        {
            if (await stream.ReadAsync(one, ct) == 0)
                throw new IOException("прокси закрыл соединение без ответа");

            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n')
                break;
        }

        var statusLine = Encoding.ASCII.GetString([.. head]).Split("\r\n", 2)[0];
        var parts = statusLine.Split(' ', 3);
        return parts.Length >= 2 && parts[0].StartsWith("HTTP/", StringComparison.Ordinal) && int.TryParse(parts[1], out var code)
            ? code
            : throw new IOException($"непонятный ответ прокси «{Shorten(statusLine)}»");
    }

    private static string Shorten(string line) => line.Length > 60 ? line[..60] : line;
}
