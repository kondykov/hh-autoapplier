using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HhAutoApplier.Application.Commands.Links;
using HhAutoApplier.Application.Commands.Notifications;
using HhAutoApplier.Domain.Links;
using HhAutoApplier.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Tests.Api;

// Связь через VPN (#132): журнал сбоев по итогам проверок, досылка ждущих уведомлений после восстановления, GET /api/health/links.
// Проверки подаются прямо в обработчик, как их подаёт LinkMonitorHostedService
[Collection(ApiCollection.Name)]
public class LinksApiTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _admin = factory.CreateApiClient();

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        await factory.WithDbAsync(db => db.Notifications.ExecuteDeleteAsync());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly LinkProbe ProxyOk = new(LinkProbe.Proxy, false, LinkState.Ok);

    private static LinkProbe Via(string link, LinkState state) => new(link, true, state, state == LinkState.Ok ? null : $"прокси: {state}");

    private async Task<LinkCheckResult> Check(params LinkProbe[] probes)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RecordLinkCheckCommandHandler>().Handle(new RecordLinkCheckCommand(probes));
    }

    private async Task<ResumeWaitingNotificationsResult> Resume(DateTimeOffset okAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ResumeWaitingNotificationsCommandHandler>().Handle(new ResumeWaitingNotificationsCommand(okAt));
    }

    private Task<List<LinkOutage>> Outages() => factory.WithDbAsync(db => db.LinkOutages.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    private Task<List<Notification>> Notifications() => factory.WithDbAsync(db => db.Notifications.AsNoTracking().OrderBy(n => n.Id).ToListAsync());

    private Task<Guid> AdminId() => factory.WithDbAsync(db => db.Users.Where(u => u.Email == ApiFactory.AdminEmail).Select(u => u.Id).SingleAsync());

    [Fact]
    public async Task Outages_DirectionThenVpnNode_EndOnRecovery()
    {
        // Случай 2026-11-18: сначала только Telegram — «направление»
        var first = await Check(ProxyOk, Via(LinkProbe.Telegram, LinkState.BadGateway), Via("api.groq.com", LinkState.Ok));
        Assert.True(first.Verdict.Failing);
        Assert.False(first.TelegramOk);
        var telegram = Assert.Single(await Outages());
        Assert.Equal((LinkProbe.Telegram, OutageScope.Direction, LinkState.BadGateway), (telegram.Subject, telegram.Scope, telegram.Reason));

        // Повтор того же сбоя — тот же сбой, не новый
        Assert.Empty((await Check(ProxyOk, Via(LinkProbe.Telegram, LinkState.BadGateway), Via("api.groq.com", LinkState.Ok))).Started);
        Assert.Single(await Outages());

        // Отказали все направления через прокси — «узел VPN»; сбой Telegram остаётся открытым
        var node = await Check(ProxyOk, Via(LinkProbe.Telegram, LinkState.BadGateway), Via("api.groq.com", LinkState.BadGateway));
        Assert.Equal(LinkClassifier.VpnNode, Assert.Single(node.Started).Subject);
        Assert.All(await Outages(), o => Assert.True(o.Open));

        // Всё ожило: оба сбоя закрыты
        var recovered = await Check(ProxyOk, Via(LinkProbe.Telegram, LinkState.Ok), Via("api.groq.com", LinkState.Ok));
        Assert.True(recovered.TelegramOk);
        Assert.False(recovered.Verdict.Failing);
        Assert.Equal([LinkProbe.Telegram, LinkClassifier.VpnNode], recovered.Ended.Order(StringComparer.Ordinal));
        Assert.All(await Outages(), o => Assert.False(o.Open));
    }

    [Fact]
    public async Task Outages_ProxyDown_DirectionsUntouched_RemovedDirectionCloses()
    {
        await Check(ProxyOk, Via(LinkProbe.Telegram, LinkState.Timeout), Via("api.groq.com", LinkState.Ok));

        // Лежит прокси: про Telegram ничего не сказать — его сбой не закрывается и не дублируется
        var down = await Check(new LinkProbe(LinkProbe.Proxy, false, LinkState.ProxyUnreachable, "порт не отвечает"),
            Via(LinkProbe.Telegram, LinkState.ProxyUnreachable), Via("api.groq.com", LinkState.ProxyUnreachable));
        Assert.Equal(LinkProbe.Proxy, Assert.Single(down.Started).Subject);
        Assert.Empty(down.Ended);
        Assert.Equal(2, (await Outages()).Count(o => o.Open));

        // Провайдер убран из настроек, проверяется только ГигаЧат напрямую: сбои прежних направлений закрываются
        var direct = await Check(new LinkProbe("gigachat.devices.sberbank.ru", false, LinkState.Ok));
        Assert.Equal(2, direct.Ended.Count);
        Assert.DoesNotContain(await Outages(), o => o.Open);
    }

    // Уведомление, исчерпавшее попытки из-за связи в waitingAt
    private static Notification Waiting(Guid userId, NotificationKind kind, string key, string text, DateTimeOffset createdAt, DateTimeOffset waitingAt)
    {
        var notification = Notification.Create(userId, kind, key, text, createdAt);
        for (var i = 0; i < Notification.MaxAttempts; i++)
            notification.MarkLinkFailed("The proxy tunnel request to proxy failed with status code '502'", waitingAt);
        return notification;
    }

    [Fact]
    public async Task Resume_AfterRecovery_ImportantOneByOne_OrdinaryInDigest_StaleExpired_NoDuplicates()
    {
        var admin = await AdminId();
        var now = DateTimeOffset.UtcNow;
        var outageAt = now.AddHours(-2);
        await factory.WithDbAsync(db =>
        {
            db.Notifications.AddRange(
                Waiting(admin, NotificationKind.Captcha, "captcha:1", "🧩 Капча на hh.ru", outageAt, outageAt),
                Waiting(admin, NotificationKind.Review, "review:1", "📝 C# разработчик — Acme (LLM 55)", outageAt, outageAt),
                Waiting(admin, NotificationKind.Review, "review:2", "📝 .NET разработчик — Beta (LLM 60)\nподробности", outageAt.AddMinutes(5), outageAt),
                // Старше суток — не слать
                Waiting(admin, NotificationKind.SourceBlocked, "source-blocked:old", "Источник заблокировал сбор", now.AddHours(-25), outageAt),
                // Ждёт с момента после успешной проверки — до следующей
                Waiting(admin, NotificationKind.DailyLimit, "daily:late", "Дневной лимит", now, now.AddMinutes(1)));
            // Отклик, вошедший в сводку проверки (NotifyReviews), — не трогать
            var covered = Notification.Create(admin, NotificationKind.Review, "review:3", "в сводке", outageAt);
            covered.Cancel("в сводке");
            db.Notifications.Add(covered);
            return db.SaveChangesAsync();
        });

        Assert.Equal(5, (await Notifications()).Count(n => n.Waiting));
        var result = await Resume(now);
        Assert.Equal((1, 2, 1), (result.Resumed, result.Digested, result.Expired));

        var all = (await Notifications()).ToDictionary(n => n.Key);
        Assert.True(all["captcha:1"].Pending);
        Assert.Equal(0, all["captcha:1"].Attempts);
        Assert.All([all["review:1"], all["review:2"]], n => Assert.Equal(Notification.InMissedDigest, n.Error));
        Assert.Equal(Notification.Expired, all["source-blocked:old"].Error);
        Assert.False(all["source-blocked:old"].Pending);
        Assert.True(all["daily:late"].Waiting);
        Assert.Equal("в сводке", all["review:3"].Error);
        Assert.Null(all["review:3"].WaitingSince);

        var digest = Assert.Single(all.Values, n => n.Kind == NotificationKind.MissedDigest);
        Assert.True(digest.Pending);
        Assert.Equal(admin, digest.UserId);
        Assert.StartsWith("📭 Пока не было связи", digest.Text);
        Assert.Contains("C# разработчик — Acme", digest.Text);
        Assert.Contains(".NET разработчик — Beta (LLM 60)", digest.Text);
        Assert.DoesNotContain("подробности", digest.Text);

        // Повторная проверка (или перезапуск сервера) ничего не дублирует
        Assert.Equal(new ResumeWaitingNotificationsResult(0, 0, 0), await Resume(now));
        // Шесть записанных и одна сводка пропущенного
        Assert.Equal(7, (await Notifications()).Count);
    }

    [Fact]
    public async Task HealthLinks_AdminOnly_ShowsStateAndOutages()
    {
        await Check(ProxyOk, Via(LinkProbe.Telegram, LinkState.BadGateway), Via("api.groq.com", LinkState.Ok));
        var admin = await AdminId();
        await factory.WithDbAsync(db =>
        {
            db.Notifications.Add(Waiting(admin, NotificationKind.Captcha, "captcha:w", "Капча", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            return db.SaveChangesAsync();
        });

        var response = await _admin.GetAsync("/api/health/links");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var links = body.GetProperty("links").EnumerateArray().ToDictionary(l => l.GetProperty("link").GetString()!);
        Assert.Equal("BadGateway", links[LinkProbe.Telegram].GetProperty("state").GetString());
        Assert.Equal("Ok", links["api.groq.com"].GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, links["api.groq.com"].GetProperty("lastOkAt").ValueKind);
        var open = Assert.Single(body.GetProperty("open").EnumerateArray());
        Assert.Equal(("Telegram", "Direction"), (open.GetProperty("subject").GetString(), open.GetProperty("scope").GetString()));
        Assert.Equal(1, body.GetProperty("waitingNotifications").GetInt32());

        // Обычный пользователь и ключ расширения (роль User) — 403; без входа — 401; открытый /health не изменился
        await _admin.PostAsJsonAsync("/api/users", new { email = "user@test.local", password = "user-password-1", role = "User" });
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateLoggedInClient("user@test.local", "user-password-1").GetAsync("/api/health/links")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateTokenClient().GetAsync("/api/health/links")).StatusCode);
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/health/links")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }
}
