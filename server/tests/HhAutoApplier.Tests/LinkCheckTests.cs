using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HhAutoApplier.Domain;
using HhAutoApplier.Domain.Links;
using HhAutoApplier.Domain.Notifications;
using HhAutoApplier.Infrastructure.Links;
using HhAutoApplier.Infrastructure.Telegram;
using Telegram.Bot.Exceptions;

namespace HhAutoApplier.Tests;

// Проверка связи (#132): зонд через фейковый прокси на TcpListener, разбор «узел VPN / направление», журнал сбоев,
// ожидание связи у уведомления. Без интернета и Docker
public class LinkCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 17, 15, 0, TimeSpan.Zero);

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(500);

    // Сертификат «сервиса» за прокси: проверка сертификата в тесте подменена
    private static readonly X509Certificate2 Certificate = CreateCertificate();

    private static readonly RemoteCertificateValidationCallback AnyCertificate = (_, _, _, _) => true;

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=api.telegram.org", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SslStream на Windows требует ключ из PFX
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    // Фейковый прокси: принимает подключение, читает CONNECT и отвечает как скажет тест
    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private readonly Task _loop;

        private readonly CancellationTokenSource _stop = new();

        public FakeProxy(Func<NetworkStream, string, CancellationToken, Task> respond)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    // Остановка: отмена или уже закрытый слушатель
                    catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                    {
                        return;
                    }

                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var request = await ReadHeadAsync(stream, _stop.Token);
                            Requests.Enqueue(request.Split("\r\n")[0]);
                            try
                            {
                                await respond(stream, request, _stop.Token);
                            }
                            catch (Exception) when (_stop.IsCancellationRequested)
                            {
                            }
                            catch (IOException)
                            {
                            }
                        }
                    });
                }
            });
        }

        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();

        public Uri Uri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, ct) > 0)
                head.Append((char)one[0]);
            return head.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            await _loop;
        }
    }

    private static Task Reply(NetworkStream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n"), ct).AsTask();

    private static LinkTarget Telegram(Uri proxy) => new(LinkProbe.Telegram, "api.telegram.org", 443, proxy);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Probe_ConnectAndTls_Ok()
    {
        await using var proxy = new FakeProxy(async (stream, _, ct) =>
        {
            await Reply(stream, "200 Connection established", ct);
            // Туннель: дальше «сервис» — TLS-сервер на том же соединении
            await using var tls = new SslStream(stream);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, ct);
        });

        var probe = await new LinkProber(TimeSpan.FromSeconds(5), AnyCertificate).ProbeAsync(Telegram(proxy.Uri), CancellationToken.None);

        Assert.Equal(LinkState.Ok, probe.State);
        Assert.True(probe.ViaProxy);
        Assert.Equal("CONNECT api.telegram.org:443 HTTP/1.1", Assert.Single(proxy.Requests));
    }

    [Fact]
    public async Task Probe_UntrustedCertificate_IsError()
    {
        await using var proxy = new FakeProxy(async (stream, _, ct) =>
        {
            await Reply(stream, "200 Connection established", ct);
            await using var tls = new SslStream(stream);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, ct);
        });

        // Без подмены сертификат проверяется, как у обычного запроса: самоподписанный «Telegram» — не связь
        var probe = await new LinkProber(TimeSpan.FromSeconds(5)).ProbeAsync(Telegram(proxy.Uri), CancellationToken.None);

        Assert.Equal(LinkState.Error, probe.State);
        Assert.StartsWith("TLS", probe.Detail);
    }

    [Fact]
    public async Task Probe_BadGatewayOnConnect()
    {
        await using var proxy = new FakeProxy((stream, _, ct) => Reply(stream, "502 Bad Gateway", ct));

        var probe = await new LinkProber(Short, AnyCertificate).ProbeAsync(Telegram(proxy.Uri), CancellationToken.None);

        Assert.Equal(LinkState.BadGateway, probe.State);
        Assert.Contains("502", probe.Detail);
    }

    [Fact]
    public async Task Probe_OtherProxyStatus_IsError()
    {
        await using var proxy = new FakeProxy((stream, _, ct) => Reply(stream, "407 Proxy Authentication Required", ct));

        var probe = await new LinkProber(Short, AnyCertificate).ProbeAsync(Telegram(proxy.Uri), CancellationToken.None);

        Assert.Equal(LinkState.Error, probe.State);
        Assert.Contains("407", probe.Detail);
    }

    [Fact]
    public async Task Probe_ProxySilent_Timeout()
    {
        await using var proxy = new FakeProxy((_, _, ct) => Task.Delay(Timeout.Infinite, ct));

        var started = DateTime.UtcNow;
        var probe = await new LinkProber(Short, AnyCertificate).ProbeAsync(Telegram(proxy.Uri), CancellationToken.None);

        Assert.Equal(LinkState.Timeout, probe.State);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Probe_ProxyPortClosed_ProxyUnreachable()
    {
        var closed = new Uri($"http://127.0.0.1:{FreePort()}");
        var prober = new LinkProber(Short, AnyCertificate);

        Assert.Equal(LinkState.ProxyUnreachable, (await prober.ProbeAsync(Telegram(closed), CancellationToken.None)).State);
        var self = await prober.ProbeProxyAsync(closed, CancellationToken.None);
        Assert.Equal(LinkProbe.Proxy, self.Link);
        Assert.Equal(LinkState.ProxyUnreachable, self.State);

        await using var proxy = new FakeProxy((_, _, _) => Task.CompletedTask);
        Assert.Equal(LinkState.Ok, (await prober.ProbeProxyAsync(proxy.Uri, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Probe_Direct_PortClosed_IsError()
    {
        // Windows повторяет подключение к закрытому порту ~2 с, Linux отказывает сразу
        var probe = await new LinkProber(TimeSpan.FromSeconds(10), AnyCertificate).ProbeAsync(new LinkTarget("local", "127.0.0.1", FreePort(), null), CancellationToken.None);

        Assert.False(probe.ViaProxy);
        Assert.Equal(LinkState.Error, probe.State);
    }

    [Fact]
    public void Targets_ProxyAndBypass_LikeHttpClient()
    {
        var proxy = new WebProxy("http://user:secret@host.docker.internal:10810", false, ["gigachat\\.devices\\.sberbank\\.ru"]);
        var targets = new LinkTargets(LinkTargets.Endpoints(true, ["https://api.groq.com/openai/v1", "https://gigachat.devices.sberbank.ru/api/v1", "https://api.groq.com/x"]), proxy);

        Assert.Equal([LinkProbe.Telegram, "api.groq.com", "gigachat.devices.sberbank.ru"], targets.Directions.Select(t => t.Link));
        Assert.NotNull(targets.Directions[0].Proxy);
        Assert.Null(targets.Directions[2].Proxy);
        // Учётные данные прокси — не для людей
        Assert.Equal("host.docker.internal:10810", targets.ProxyDisplay);

        var direct = new LinkTargets(LinkTargets.Endpoints(false, []), null);
        Assert.False(direct.Any);
        Assert.Null(direct.ProxyDisplay);
    }

    private static LinkProbe Via(string link, LinkState state) => new(link, true, state, state.ToString());

    private static readonly LinkProbe ProxyOk = new(LinkProbe.Proxy, false, LinkState.Ok);

    [Fact]
    public void Classify_AllViaProxyFail_VpnNode()
    {
        var verdict = LinkClassifier.Classify([ProxyOk, Via("Telegram", LinkState.BadGateway), Via("api.groq.com", LinkState.BadGateway)]);

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal(LinkClassifier.VpnNode, failure.Subject);
        Assert.Equal(OutageScope.VpnNode, failure.Scope);
        Assert.Equal(LinkState.BadGateway, failure.Reason);
        // Направления по отдельности не судятся: про каждое по такой проверке ничего не сказать
        Assert.DoesNotContain("Telegram", verdict.Judged);
    }

    [Fact]
    public void Classify_OneViaProxyFails_Direction()
    {
        // Случай 2026-11-18: Telegram — Bad Gateway, Groq работает
        var verdict = LinkClassifier.Classify([ProxyOk, Via("Telegram", LinkState.BadGateway), Via("api.groq.com", LinkState.Ok)]);

        var failure = Assert.Single(verdict.Failures);
        Assert.Equal(("Telegram", OutageScope.Direction), (failure.Subject, failure.Scope));
        Assert.Contains(LinkClassifier.VpnNode, verdict.Judged);
        Assert.Contains("api.groq.com", verdict.Judged);
    }

    [Fact]
    public void Classify_SingleViaProxy_IsDirection_AndMixedReasons()
    {
        // Одно направление через прокси: «все» отказали, но узел от направления не отличить
        Assert.Equal(OutageScope.Direction, Assert.Single(LinkClassifier.Classify([ProxyOk, Via("Telegram", LinkState.Timeout)]).Failures).Scope);

        var mixed = Assert.Single(LinkClassifier.Classify([Via("Telegram", LinkState.Timeout), Via("api.groq.com", LinkState.BadGateway)]).Failures);
        Assert.Equal((OutageScope.VpnNode, LinkState.Error), (mixed.Scope, mixed.Reason));
    }

    [Fact]
    public void Classify_ProxyDown_OnlyProxy_DirectStillJudged()
    {
        var verdict = LinkClassifier.Classify([
            new LinkProbe(LinkProbe.Proxy, false, LinkState.ProxyUnreachable),
            Via("Telegram", LinkState.ProxyUnreachable),
            Via("api.groq.com", LinkState.ProxyUnreachable),
            new LinkProbe("gigachat.devices.sberbank.ru", false, LinkState.Timeout),
        ]);

        Assert.Equal(["gigachat.devices.sberbank.ru", LinkProbe.Proxy], verdict.Failures.Select(f => f.Subject).Order());
        Assert.DoesNotContain(LinkClassifier.VpnNode, verdict.Judged);
        Assert.False(LinkClassifier.Classify([ProxyOk, Via("Telegram", LinkState.Ok)]).Failing);
    }

    [Fact]
    public void Outage_StartContinueEnd()
    {
        var outage = LinkOutage.Start(new LinkFailure("Telegram", OutageScope.Direction, LinkState.BadGateway, "502"), Now);
        Assert.True(outage.Open);

        outage.Continue(new LinkFailure("Telegram", OutageScope.Direction, LinkState.Timeout, new string('x', 600)), Now.AddMinutes(1));
        Assert.Equal(LinkState.Timeout, outage.Reason);
        Assert.Equal(LinkOutage.MaxDetailLength, outage.Detail!.Length);
        Assert.Equal(Now.AddMinutes(1), outage.LastSeenAt);

        Assert.Throws<DomainException>(() => outage.Continue(new LinkFailure("api.groq.com", OutageScope.Direction, LinkState.Timeout, null), Now));
        // Конец не раньше последней проверки со сбоем
        outage.End(Now);
        Assert.Equal(Now.AddMinutes(1), outage.EndedAt);
        Assert.Throws<DomainException>(() => outage.End(Now.AddMinutes(2)));
        Assert.Throws<DomainException>(() => outage.Continue(new LinkFailure("Telegram", OutageScope.Direction, LinkState.Timeout, null), Now));

        Assert.Throws<DomainException>(() => LinkOutage.Start(new LinkFailure(" ", OutageScope.Direction, LinkState.Timeout, null), Now));
        Assert.Throws<DomainException>(() => LinkOutage.Start(new LinkFailure("Telegram", OutageScope.Direction, LinkState.Ok, null), Now));
    }

    [Fact]
    public void Notification_LinkFailures_Wait_ThenResume()
    {
        var notification = Notification.Create(Guid.NewGuid(), NotificationKind.Captcha, "captcha:1", "Капча", Now);
        for (var i = 0; i < Notification.MaxAttempts - 1; i++)
            notification.MarkLinkFailed("Bad Gateway", Now.AddSeconds(i));
        Assert.False(notification.Waiting);

        notification.MarkLinkFailed("Bad Gateway", Now.AddMinutes(1));
        Assert.True(notification.Waiting);
        Assert.False(notification.Pending);
        Assert.Equal(Now.AddMinutes(1), notification.WaitingSince);

        Assert.True(notification.Resume(Now.AddHours(2)));
        Assert.True(notification.Pending);
        Assert.False(notification.Waiting);
        Assert.Throws<DomainException>(() => notification.Resume(Now.AddHours(2)));
    }

    [Fact]
    public void Notification_RefusedByTelegram_NotWaiting_StaleExpires()
    {
        var refused = Notification.Create(Guid.NewGuid(), NotificationKind.Captcha, "captcha:1", "Капча", Now);
        for (var i = 0; i < Notification.MaxAttempts; i++)
            refused.MarkFailed("Bad Request");
        Assert.False(refused.Waiting);

        var stale = Notification.Create(Guid.NewGuid(), NotificationKind.Captcha, "captcha:2", "Капча", Now);
        for (var i = 0; i < Notification.MaxAttempts; i++)
            stale.MarkLinkFailed("timeout", Now);
        Assert.False(stale.Resume(Now + Notification.MaxWait + TimeSpan.FromMinutes(1)));
        Assert.Equal(Notification.Expired, stale.Error);
        Assert.False(stale.Pending);
        Assert.False(stale.Waiting);

        var digest = Notification.Create(Guid.NewGuid(), NotificationKind.Review, "review:1", "Отклик", Now);
        for (var i = 0; i < Notification.MaxAttempts; i++)
            digest.MarkLinkFailed("timeout", Now);
        digest.CoverByDigest();
        Assert.Equal(Notification.InMissedDigest, digest.Error);
        Assert.False(digest.Pending || digest.Waiting);
    }

    [Fact]
    public void LinkFailure_NetworkAndServerErrors_NotBadRequests()
    {
        Assert.True(TelegramHostedService.IsLinkFailure(new HttpRequestException("The proxy tunnel request to proxy failed with status code '502'")));
        Assert.True(TelegramHostedService.IsLinkFailure(new RequestException("Request timed out", new TaskCanceledException())));
        Assert.True(TelegramHostedService.IsLinkFailure(new ApiRequestException("Bad Gateway", 502)));
        Assert.True(TelegramHostedService.IsLinkFailure(new ApiRequestException("Too Many Requests", 429)));
        Assert.False(TelegramHostedService.IsLinkFailure(new ApiRequestException("Bad Request: message text is empty", 400)));
        Assert.False(TelegramHostedService.IsLinkFailure(new InvalidOperationException("bug")));
    }
}
