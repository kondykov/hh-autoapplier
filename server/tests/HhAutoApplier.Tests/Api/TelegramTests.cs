using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HhAutoApplier.Application.Commands.Notifications;
using HhAutoApplier.Application.Commands.Telegram;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Domain.Llm;
using HhAutoApplier.Domain.Notifications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Tests.Api;

// Telegram (#28): привязка чата кодом, команды и кнопки бота, уведомления в outbox. Транспорт Telegram не участвует:
// сообщения и нажатия подаются прямо в обработчики, как их подаёт TelegramHostedService
[Collection(ApiCollection.Name)]
public class TelegramTests(ApiFactory factory) : IAsyncLifetime
{
    private const long OwnerChat = 100;

    private const long UserChat = 200;

    private const string UserPassword = "user-password-1";

    private readonly HttpClient _admin = factory.CreateApiClient();

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        factory.Telegram.Configured = true;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private Task<IReadOnlyList<BotMessage>> Send(long chat, string text, bool isPrivate = true) =>
        InScope(sp => sp.GetRequiredService<HandleTelegramMessageCommandHandler>()
            .Handle(new HandleTelegramMessageCommand { ChatId = chat, IsPrivate = isPrivate, Text = text, Username = "owner" }));

    private Task<BotCallbackResult> Press(long chat, string data) =>
        InScope(sp => sp.GetRequiredService<HandleTelegramCallbackCommandHandler>().Handle(new HandleTelegramCallbackCommand { ChatId = chat, Data = data }));

    private Task<int> NotifyReviews() => InScope(sp => sp.GetRequiredService<NotifyReviewsCommandHandler>().Handle(new NotifyReviewsCommand()));

    private Task<List<Notification>> Notifications() => factory.WithDbAsync(db => db.Notifications.OrderBy(n => n.Id).ToListAsync());

    private static async Task<string> Code(HttpClient client) =>
        (await Json(await client.PostAsync("/api/telegram/link", null))).GetProperty("code").GetString()!;

    private async Task Link(HttpClient client, long chat) => await Send(chat, $"/start {await Code(client)}");

    private async Task<HttpClient> CreateUser(string email)
    {
        await Json(await _admin.PostAsJsonAsync("/api/users", new { email, password = UserPassword, role = "User" }));
        return factory.CreateLoggedInClient(email, UserPassword);
    }

    // Сомнительные отклики администратора: 1…count ждут ручной проверки
    private async Task CreateReviews(int count = 2)
    {
        factory.Llm.Configured = true;
        await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#" }));
        object Card(string id) => new { id, title = $"C# разработчик {id}", company = "Acme", description = "", salaryCurrency = "RUR", workFormat = "Удалённо" };
        await Json(await _admin.PostAsJsonAsync("/api/import", new { search = "dotnet", vacancies = Enumerable.Range(1, count).Select(i => Card($"{i}")).ToArray() }));
        await factory.ConfirmImportedAsync();
        for (var i = 0; i < count; i++)
            factory.Llm.Answers.Enqueue("""{"verdict": "doubtful", "score": 55, "resume": ".NET", "reason": "мало опыта"}""");
        await Json(await _admin.PostAsJsonAsync("/api/llm/evaluate", new { limit = 10 }));
    }

    private Task<VacancyStatus> StatusOf(string id) =>
        factory.WithDbAsync(db => db.JobApplications.Where(a => a.VacancyId == id).Select(a => a.Status).SingleAsync());

    [Fact]
    public async Task Link_AfterPasswordLogin_CodeOnce_Unlink()
    {
        factory.Telegram.Configured = false;
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsync("/api/telegram/link", null)).StatusCode);
        factory.Telegram.Configured = true;

        // Токеном код не получить: чат получает право решать за пользователя
        Assert.Equal(HttpStatusCode.Conflict, (await factory.CreateTokenClient().PostAsync("/api/telegram/link", null)).StatusCode);

        var link = await Json(await _admin.PostAsync("/api/telegram/link", null));
        var code = link.GetProperty("code").GetString()!;
        Assert.Equal($"https://t.me/test_bot?start={code}", link.GetProperty("link").GetString());
        Assert.False((await Json(await _admin.GetAsync("/api/telegram"))).GetProperty("linked").GetBoolean());

        var reply = Assert.Single(await Send(OwnerChat, $"/start {code}"));
        Assert.Contains(ApiFactory.AdminEmail, reply.Text);
        var state = await Json(await _admin.GetAsync("/api/telegram"));
        Assert.True(state.GetProperty("linked").GetBoolean());
        Assert.Equal("owner", state.GetProperty("username").GetString());

        // Код одноразовый
        Assert.Contains("не найден", Assert.Single(await Send(UserChat, $"/start {code}")).Text);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync("/api/telegram")).StatusCode);
        Assert.False((await Json(await _admin.GetAsync("/api/telegram"))).GetProperty("linked").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync("/api/telegram")).StatusCode);
        Assert.Equal(BotTexts.NotLinked, Assert.Single(await Send(OwnerChat, "/status")).Text);
    }

    [Fact]
    public async Task Chat_LinkedToOneUser_RelinkMovesIt()
    {
        var user = await CreateUser("user@test.local");
        await Link(_admin, OwnerChat);
        await Link(user, OwnerChat);

        // Чат перешёл к пользователю; у администратора привязки больше нет
        Assert.False((await Json(await _admin.GetAsync("/api/telegram"))).GetProperty("linked").GetBoolean());
        Assert.True((await Json(await user.GetAsync("/api/telegram"))).GetProperty("linked").GetBoolean());
    }

    [Fact]
    public async Task Commands_OnlyInLinkedPrivateChat()
    {
        await CreateReviews();
        Assert.Equal(BotTexts.NotLinked, Assert.Single(await Send(OwnerChat, "/status")).Text);

        await Link(_admin, OwnerChat);
        Assert.Empty(await Send(OwnerChat, "/status", isPrivate: false));
        Assert.Contains("на проверке: 2", Assert.Single(await Send(OwnerChat, "/status@test_bot")).Text);
        Assert.Contains("/review", Assert.Single(await Send(OwnerChat, "привет")).Text);

        // Отвязка — только подтверждением
        var confirm = Assert.Single(await Send(OwnerChat, "/stop"));
        Assert.Equal(["unlink:", "cancel:"], confirm.Buttons.Select(b => b.Data));
        Assert.Equal("Отменено", (await Press(OwnerChat, "cancel:")).Status);
        Assert.Contains("на проверке: 2", Assert.Single(await Send(OwnerChat, "/status")).Text);

        var unlinked = await Press(OwnerChat, "unlink:");
        Assert.Contains("отвязан", unlinked.Status);
        Assert.Equal(BotMenu.Remove, Assert.Single(unlinked.Replies).Menu);
        Assert.Equal(BotTexts.NotLinked, Assert.Single(await Send(OwnerChat, "/status")).Text);
    }

    [Fact]
    public async Task Buttons_MenuOverviewAndReview()
    {
        await Link(_admin, OwnerChat);
        Assert.Equal(BotMenu.Show, Assert.Single(await Send(OwnerChat, BotTexts.HelpButton)).Menu);
        // Проверять нечего — кнопки «Решить проверку» нет
        var empty = Assert.Single(await Send(OwnerChat, BotTexts.OverviewButton));
        Assert.DoesNotContain(empty.Buttons, b => b.Data.StartsWith("rv:"));

        await CreateReviews();
        // Прежняя подпись кнопки меню («📊 Статус») работает
        var overview = Assert.Single(await Send(OwnerChat, BotTexts.OldStatusButton));
        Assert.True(overview.Html);
        Assert.Contains("на проверке: 2", overview.Text);
        var show = Assert.Single(overview.Buttons, b => b.Data == "rv:0");
        Assert.Contains("(2)", show.Text);

        // Карточка листается на месте: одна, с номером в очереди
        var pressed = await Press(OwnerChat, show.Data);
        Assert.NotNull(pressed.Edit);
        Assert.Contains("1 из 2", pressed.Edit.Text);
        Assert.Contains(pressed.Edit.Buttons, b => b.Data == "rv:1");
        Assert.Contains("2 из 2", (await Press(OwnerChat, "rv:1")).Edit!.Text);
        Assert.Contains("1 из 2", (await Press(OwnerChat, "rv:2")).Edit!.Text);

        // Кнопка сводки — карточка новым сообщением
        Assert.Contains("1 из 2", Assert.Single((await Press(OwnerChat, "review:")).Replies).Text);
        Assert.Contains("1 из 2", Assert.Single(await Send(OwnerChat, BotTexts.ReviewButton)).Text);

        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "nonsense")).Notice);
        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "zz:1")).Notice);
        Assert.Equal("Чат не привязан", (await Press(UserChat, show.Data)).Notice);
    }

    [Fact]
    public void NotificationButtons_ByKind()
    {
        static IEnumerable<string> Data(IReadOnlyList<IReadOnlyList<BotButton>> rows) => rows.SelectMany(r => r).Select(b => b.Url ?? b.Data);
        Assert.Equal(["approve:7", "decline:7", "rk:7"], Data(BotTexts.NotificationButtons(NotificationKind.Review, "review:1", "7")));
        Assert.Equal(["review:"], Data(BotTexts.NotificationButtons(NotificationKind.Review, "review-digest:5", null)));
        Assert.Equal(["https://hh.ru/chat/21", "handled:21"], Data(BotTexts.NotificationButtons(NotificationKind.EmployerReply, "dialog:21:201", null)));
        Assert.Empty(BotTexts.NotificationButtons(NotificationKind.Captcha, "captcha:1", null));
    }

    [Fact]
    public async Task Review_ApproveAndDeclineFromChat_OnlyOwnApplications()
    {
        await CreateReviews();
        await Link(_admin, OwnerChat);

        var card = Assert.Single(await Send(OwnerChat, "/review"));
        Assert.Equal(["approve", "decline"], card.Rows[0].Select(b => b.Data.Split(':')[0]));
        Assert.Contains("мало опыта", card.Text);

        // Чужой чат не решает за владельца
        await Link(await CreateUser("user@test.local"), UserChat);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "approve:1")).Notice);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "decline:1")).Notice);
        Assert.Equal(VacancyStatus.Review, await StatusOf("1"));
        Assert.Equal("Откликов на проверке нет", Assert.Single(await Send(UserChat, "/review")).Text);

        var approved = await Press(OwnerChat, "approve:1");
        Assert.Equal("Одобрено", approved.Notice);
        // Решённое сообщение — с кнопкой к следующему
        Assert.Equal("rv:0", Assert.Single(approved.StatusRows.SelectMany(r => r)).Data);
        Assert.Equal(VacancyStatus.Queued, await StatusOf("1"));
        Assert.Equal("Уже решено", (await Press(OwnerChat, "decline:1")).Notice);

        var declined = await Press(OwnerChat, "decline:2");
        Assert.Equal("❌ Отклонено", declined.Status);
        Assert.Empty(declined.StatusRows);
        Assert.Equal(VacancyStatus.Skipped, await StatusOf("2"));
        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "delete:2")).Notice);
        Assert.Equal("Откликов на проверке нет", Assert.Single(await Send(OwnerChat, "/review")).Text);
    }

    [Fact]
    public async Task ReviewNotifications_OncePerApplication_OnlyToLinkedChat()
    {
        await CreateReviews();
        Assert.Equal(0, await NotifyReviews());

        await Link(_admin, OwnerChat);
        Assert.Equal(2, await NotifyReviews());
        Assert.Equal(0, await NotifyReviews());

        var sent = await Notifications();
        Assert.All(sent, n => Assert.Equal(NotificationKind.Review, n.Kind));
        Assert.Equal(["1", "2"], sent.Select(n => n.VacancyId).Order());
        Assert.All(sent, n => Assert.Null(n.SentAt));
    }

    [Fact]
    public async Task ReviewNotifications_ManyAtOnce_OneDigest_NotRepeatedOneByOne()
    {
        await Link(_admin, OwnerChat);
        await CreateReviews(5);

        Assert.Equal(1, await NotifyReviews());
        Assert.Equal(0, await NotifyReviews());

        var sent = await Notifications();
        var digest = Assert.Single(sent, n => n.Attempts < Notification.MaxAttempts);
        Assert.Contains("На проверке новых: 5 (всего 5)", digest.Text);
        Assert.Contains("C# разработчик 3", digest.Text);
        Assert.Contains("/review", digest.Text);
        Assert.Null(digest.VacancyId);
        // Отклики сводки по одному не придут: их уведомления сразу отменены
        Assert.Equal(5, sent.Count(n => !n.Pending && n.Key.StartsWith("review:")));
    }

    [Fact]
    public async Task ExtensionEvents_ToOwner_Deduplicated()
    {
        var key = factory.CreateTokenClient();
        var captcha = new { kind = "Captcha", message = "vacancy 1" };

        // Чат не привязан — доставить некому
        Assert.False((await Json(await key.PostAsJsonAsync("/api/events", captcha))).GetProperty("notified").GetBoolean());

        await Link(_admin, OwnerChat);
        Assert.True((await Json(await key.PostAsJsonAsync("/api/events", captcha))).GetProperty("notified").GetBoolean());
        Assert.False((await Json(await key.PostAsJsonAsync("/api/events", captcha))).GetProperty("notified").GetBoolean());
        Assert.True((await Json(await key.PostAsJsonAsync("/api/events", new { kind = "DailyLimit" }))).GetProperty("notified").GetBoolean());
        // #95: слетел вход — одно сообщение в час
        Assert.True((await Json(await key.PostAsJsonAsync("/api/events", new { kind = "SignedOut" }))).GetProperty("notified").GetBoolean());
        Assert.False((await Json(await key.PostAsJsonAsync("/api/events", new { kind = "SignedOut" }))).GetProperty("notified").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await key.PostAsJsonAsync("/api/events", new { kind = "Unknown" })).StatusCode);

        var sent = await Notifications();
        Assert.Equal([NotificationKind.Captcha, NotificationKind.DailyLimit, NotificationKind.SignedOut], sent.Select(n => n.Kind));
        Assert.Contains("vacancy 1", sent[0].Text);
        Assert.Contains("Нет входа в hh.ru", sent[2].Text);

        factory.Telegram.Configured = false;
        Assert.False((await Json(await key.PostAsJsonAsync("/api/events", new { kind = "Captcha" }))).GetProperty("notified").GetBoolean());
    }

    [Fact]
    public async Task ServerFailures_OnlyToAdmins()
    {
        await Link(_admin, OwnerChat);
        await Link(await CreateUser("user@test.local"), UserChat);

        await InScope(async sp =>
        {
            await sp.GetRequiredService<Notifier>().NotifyAdminsAsync(NotificationKind.SourceBlocked, "source-blocked:test", "капча");
            return 0;
        });

        var sent = Assert.Single(await Notifications());
        var adminId = await factory.WithDbAsync(db => db.Users.Where(u => u.Email == ApiFactory.AdminEmail).Select(u => u.Id).SingleAsync());
        Assert.Equal(adminId, sent.UserId);
    }

    // #56: исчерпавшему долю LLM — одно сообщение в сутки, сколько бы проходов ни было
    [Fact]
    public async Task LlmShareExhausted_OneNotificationPerDay()
    {
        var client = await CreateUser("quota@test.local");
        await Link(client, UserChat);
        var user = await factory.WithDbAsync(db => db.Users.Where(u => u.Email == "quota@test.local").Select(u => u.Id).SingleAsync());
        await factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < 150; i++)
                db.LlmCalls.Add(LlmCall.Succeeded("test", null, "model", "p", "r", 1, 1, DateTimeOffset.UtcNow).For(user));
            return await db.SaveChangesAsync();
        });

        for (var pass = 0; pass < 2; pass++)
            await InScope(async sp =>
            {
                await sp.GetRequiredService<LlmUserQuota>().NotifyExhaustedAsync(default);
                return true;
            });

        var sent = await factory.WithDbAsync(db => db.Notifications.Where(n => n.Key.StartsWith("llm-quota:")).ToListAsync());
        Assert.Equal(user, Assert.Single(sent).UserId);
    }
}
