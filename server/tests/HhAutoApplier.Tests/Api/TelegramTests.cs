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

    private Task<BotResult> Raw(long chat, string text, bool isPrivate = true, int messageId = 0) =>
        InScope(sp => sp.GetRequiredService<HandleTelegramMessageCommandHandler>()
            .Handle(new HandleTelegramMessageCommand { ChatId = chat, IsPrivate = isPrivate, Text = text, Username = "owner", MessageId = messageId }));

    // Что увидит пользователь: сообщения (кроме служебных) и экран
    private async Task<IReadOnlyList<BotMessage>> Send(long chat, string text, bool isPrivate = true)
    {
        var result = await Raw(chat, text, isPrivate);
        return [.. result.Messages.Where(m => !m.DeleteAfterSend), .. result.Screen is { } s ? [s] : Array.Empty<BotMessage>()];
    }

    private Task<BotResult> Press(long chat, string data, int messageId = 0) =>
        InScope(sp => sp.GetRequiredService<HandleTelegramCallbackCommandHandler>()
            .Handle(new HandleTelegramCallbackCommand { ChatId = chat, Data = data, MessageId = messageId }));

    private Task<bool> Remember(long chat, int messageId) =>
        InScope(sp => sp.GetRequiredService<RememberTelegramScreenCommandHandler>().Handle(new RememberTelegramScreenCommand(chat, messageId)));

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

        var reply = (await Send(OwnerChat, $"/start {code}"))[0];
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
        // Просто текст без режима ввода — экран-меню с подсказкой, сообщение убирается
        var text = await Raw(OwnerChat, "привет", messageId: 77);
        Assert.Contains("/menu", text.Screen!.Text);
        Assert.Equal([77], text.Delete);
        Assert.True(text.MoveScreen);

        // Отвязка — только подтверждением
        var confirm = Assert.Single(await Send(OwnerChat, "/stop"));
        Assert.Equal(["unlink:", "cancel:"], confirm.Buttons.Select(b => b.Data));
        Assert.Equal("Отменено", (await Press(OwnerChat, "cancel:")).Notice);
        Assert.Contains("на проверке: 2", Assert.Single(await Send(OwnerChat, "/status")).Text);

        var unlinked = await Press(OwnerChat, "unlink:");
        Assert.Contains("отвязан", unlinked.Screen!.Text);
        Assert.Equal(BotTexts.NotLinked, Assert.Single(await Send(OwnerChat, "/status")).Text);
    }

    [Fact]
    public async Task Screen_MenuMovesDown_ButtonsEditInPlace()
    {
        await Link(_admin, OwnerChat);
        var menu = await Raw(OwnerChat, "/menu", messageId: 10);
        // Экран — заново внизу чата, команда убирается, клавиатура прежних версий снимается служебным сообщением
        Assert.True(menu.MoveScreen);
        Assert.Equal([10], menu.Delete);
        Assert.True(Assert.Single(menu.Messages).RemoveKeyboard);
        Assert.Contains(menu.Screen!.Buttons, b => b.Data == "rv:0");

        // Транспорт запомнил экран; нажатие на нём перерисовывает его же
        await Remember(OwnerChat, 500);
        Assert.Equal(500, (await Raw(OwnerChat, "/menu", messageId: 11)).ScreenMessageId);
        var pressed = await Press(OwnerChat, "ov:", messageId: 500);
        Assert.Equal(500, pressed.ScreenMessageId);
        Assert.False(pressed.MoveScreen);
        Assert.Contains("Обзор", pressed.Screen!.Text);

        // Нажатие на старом экране — экраном становится он
        Assert.Equal(400, (await Press(OwnerChat, "menu:", messageId: 400)).ScreenMessageId);
        Assert.Equal(400, await factory.WithDbAsync(db => db.TelegramChats.Where(c => c.ChatId == OwnerChat).Select(c => c.ScreenMessageId).SingleAsync()));

        // «Открыть» под уведомлением — экран заново внизу, уведомление не трогается
        var opened = await Press(OwnerChat, "o:rv:0", messageId: 600);
        Assert.True(opened.MoveScreen);
        Assert.Equal(400, opened.ScreenMessageId);
        Assert.Null(opened.Status);
        Assert.True((await Press(OwnerChat, "ok:", messageId: 600)).DeletePressed);
    }

    [Fact]
    public async Task Buttons_MenuOverviewAndReview()
    {
        await Link(_admin, OwnerChat);
        Assert.Contains("/menu", Assert.Single(await Send(OwnerChat, BotTexts.OldHelpButton)).Text);
        // Проверять нечего — кнопки «Решить проверку» нет
        var empty = Assert.Single(await Send(OwnerChat, "/overview"));
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
        Assert.Contains("1 из 2", pressed.Screen!.Text);
        Assert.Contains(pressed.Screen.Buttons, b => b.Data == "rv:1");
        Assert.Contains("2 из 2", (await Press(OwnerChat, "rv:1")).Screen!.Text);
        Assert.Contains("1 из 2", (await Press(OwnerChat, "rv:2")).Screen!.Text);

        // Кнопка сводки прежних версий — карточка экраном заново внизу чата
        var review = await Press(OwnerChat, "review:", messageId: 900);
        Assert.True(review.MoveScreen);
        Assert.Contains("1 из 2", review.Screen!.Text);
        Assert.Contains("1 из 2", Assert.Single(await Send(OwnerChat, BotTexts.OldReviewButton)).Text);

        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "nonsense")).Notice);
        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "zz:1")).Notice);
        Assert.Equal("Чат не привязан", (await Press(UserChat, show.Data)).Notice);
    }

    [Fact]
    public void NotificationButtons_ByKind_AndDelivery()
    {
        static IEnumerable<string> Data(IReadOnlyList<IReadOnlyList<BotButton>> rows) => rows.SelectMany(r => r).Select(b => b.Url ?? b.Data);
        Assert.Equal(["https://hh.ru/chat/21", "handled:21", "o:dc:21:0:o"], Data(BotTexts.NotificationButtons(NotificationKind.EmployerReply, "dialog:21:201", null)));
        Assert.Equal(["ok:"], Data(BotTexts.NotificationButtons(NotificationKind.Captcha, "captcha:1", null)));
        Assert.Equal(["o:cs:", "ok:"], Data(BotTexts.NotificationButtons(NotificationKind.SourceBlocked, "source-blocked:1", null)));
        Assert.Empty(BotTexts.NotificationButtons(NotificationKind.Review, "review:1", "7"));
        // Обычные — в сводку, остальные — отдельным сообщением
        Assert.Equal([NotificationKind.Review, NotificationKind.Questionnaire],
            Enum.GetValues<NotificationKind>().Where(k => BotTexts.Delivery(k) == NotificationDelivery.Digest));
    }

    [Fact]
    public async Task Review_ApproveAndDeclineFromChat_OnlyOwnApplications()
    {
        await CreateReviews();
        await Link(_admin, OwnerChat);

        var card = Assert.Single(await Send(OwnerChat, "/review"));
        Assert.Equal(["ry", "rn"], card.Rows[0].Select(b => b.Data.Split(':')[0]));
        Assert.Contains("мало опыта", card.Text);

        // Чужой чат не решает за владельца — ни кнопкой экрана, ни кнопкой уведомления
        await Link(await CreateUser("user@test.local"), UserChat);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "approve:1")).Notice);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "ry:1")).Notice);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "rn:1")).Notice);
        Assert.Equal(VacancyStatus.Review, await StatusOf("1"));
        Assert.StartsWith("Откликов на проверке нет", Assert.Single(await Send(UserChat, "/review")).Text);

        // На экране: решение — всплывашкой, экран сам переходит к следующей карточке
        var first = card.Rows[0][0].Data.Split(':')[1];
        var approved = await Press(OwnerChat, $"ry:{first}");
        Assert.Equal("✅ Одобрено · осталось 1", approved.Notice);
        Assert.Contains("1 из 1", approved.Screen!.Text);
        Assert.Equal(VacancyStatus.Queued, await StatusOf(first));
        Assert.StartsWith("Уже решено", (await Press(OwnerChat, $"rn:{first}")).Notice);

        // Кнопки уведомления прежних версий — уведомление сворачивается до итога
        var other = first == "1" ? "2" : "1";
        var declined = await Press(OwnerChat, $"decline:{other}");
        Assert.Equal("❌ Отклонено", declined.Status);
        Assert.Null(declined.Screen);
        Assert.Equal(VacancyStatus.Skipped, await StatusOf(other));
        Assert.Equal("Уже решено", (await Press(OwnerChat, $"approve:{other}")).Notice);
        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "delete:2")).Notice);
        Assert.StartsWith("Откликов на проверке нет", Assert.Single(await Send(OwnerChat, "/review")).Text);
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
