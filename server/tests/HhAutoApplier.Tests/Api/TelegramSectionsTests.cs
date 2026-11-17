using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HhAutoApplier.Application.Commands.Telegram;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Tests.Api;

// Разделы бота на одном экране (#115): обзор, вакансии, проверка с правкой письма режимом ввода, ответы работодателей, поиски, сбор.
// Транспорт Telegram не участвует: сообщения и нажатия подаются прямо в обработчики, как их подаёт TelegramHostedService
[Collection(ApiCollection.Name)]
public class TelegramSectionsTests(ApiFactory factory) : IAsyncLifetime
{
    private const long OwnerChat = 100;

    private const long UserChat = 200;

    private readonly HttpClient _admin = factory.CreateApiClient();

    // Кнопки всех экранов теста — для проверки лимита callback_data
    private readonly List<BotButton> _seen = [];

    private int _messageId = 1000;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        factory.Telegram.Configured = true;
    }

    public Task DisposeAsync()
    {
        // Лимит Telegram на callback_data — 64 байта
        Assert.All(_seen.Where(b => b.Url is null), b => Assert.True(Encoding.UTF8.GetByteCount(b.Data) <= 64, b.Data));
        return Task.CompletedTask;
    }

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

    // Сообщение пользователя; MessageId — чтобы проверить, что бот его уберёт
    private async Task<BotResult> Send(long chat, string text, int? messageId = null)
    {
        var id = messageId ?? ++_messageId;
        var result = await InScope(sp => sp.GetRequiredService<HandleTelegramMessageCommandHandler>()
            .Handle(new HandleTelegramMessageCommand { ChatId = chat, IsPrivate = true, Text = text, Username = "owner", MessageId = id }));
        _seen.AddRange(result.Screen?.Buttons ?? []);
        return result;
    }

    private async Task<BotMessage> Screen(long chat, string command) => (await Send(chat, command)).Screen!;

    private async Task<BotResult> Press(long chat, string data, int messageId = 500)
    {
        var result = await InScope(sp => sp.GetRequiredService<HandleTelegramCallbackCommandHandler>()
            .Handle(new HandleTelegramCallbackCommand { ChatId = chat, Data = data, MessageId = messageId }));
        _seen.AddRange(result.Screen?.Buttons ?? []);
        _seen.AddRange(result.StatusRows.SelectMany(r => r));
        return result;
    }

    private async Task Link(HttpClient client, long chat)
    {
        var code = (await Json(await client.PostAsync("/api/telegram/link", null))).GetProperty("code").GetString()!;
        await Send(chat, $"/start {code}");
    }

    private async Task<HttpClient> CreateUser(string email)
    {
        await Json(await _admin.PostAsJsonAsync("/api/users", new { email, password = "user-password-1", role = "User" }));
        return factory.CreateLoggedInClient(email, "user-password-1");
    }

    // Отклики администратора 1…count на проверке: LLM сомневается
    private async Task CreateReviews(int count = 2, string description = "")
    {
        factory.Llm.Configured = true;
        await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#" }));
        object Card(string id) => new { id, title = $"C# <разработчик> {id}", company = "Acme & Co", description, salaryCurrency = "RUR", workFormat = "Удалённо" };
        await Json(await _admin.PostAsJsonAsync("/api/import", new { search = "dotnet", vacancies = Enumerable.Range(1, count).Select(i => Card($"{i}")).ToArray() }));
        await factory.ConfirmImportedAsync();
        for (var i = 0; i < count; i++)
            factory.Llm.Answers.Enqueue("""{"verdict": "doubtful", "score": 55, "resume": ".NET", "reason": "мало опыта"}""");
        await Json(await _admin.PostAsJsonAsync("/api/llm/evaluate", new { limit = 10 }));
    }

    private Task<JobApplication> Application(string id) =>
        factory.WithDbAsync(db => db.JobApplications.AsNoTracking().SingleAsync(a => a.VacancyId == id));

    // Режим ввода в чате — напрямую, как будто его включила кнопка (время — для проверки срока)
    private Task SetInput(long chat, string kind, string target, int? number, DateTimeOffset at) =>
        factory.WithDbAsync(async db =>
        {
            var c = await db.TelegramChats.SingleAsync(x => x.ChatId == chat);
            c.AwaitInput(kind, target, number, "menu:", at);
            return await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Overview_CountsButtonsAndCsv_CollectOnlyForAdmin()
    {
        await CreateReviews();
        await Link(_admin, OwnerChat);
        var user = await CreateUser("user@test.local");
        await Link(user, UserChat);

        var overview = await Screen(OwnerChat, "/overview");
        Assert.True(overview.Html);
        // Название из вакансии экранировано: «<» сломал бы разметку Telegram
        Assert.DoesNotContain("<разработчик>", overview.Text);
        Assert.Contains(overview.Buttons, b => b.Data == $"{VacanciesSection.List}:{(int)VacancyStatus.Review}.0.0");
        Assert.Contains(overview.Buttons, b => b.Data == "cs:");

        // Отчёт — файлом, экран не меняется
        var report = await Press(OwnerChat, "csv:");
        Assert.Null(report.Screen);
        var file = Assert.Single(report.Messages).File!;
        Assert.Equal("applications.csv", file.Name);
        Assert.StartsWith("Вакансия;", Encoding.UTF8.GetString(file.Content).TrimStart('﻿'));

        // Пользователь видит только свои счётчики, сбора у него нет
        var own = await Screen(UserChat, "/overview");
        Assert.Contains("Откликов пока нет", own.Text);
        Assert.DoesNotContain(own.Buttons, b => b.Data == "cs:");
        Assert.DoesNotContain((await Screen(UserChat, "/menu")).Buttons, b => b.Data == "cs:");
        Assert.Equal(CollectionSection.AdminOnly, (await Screen(UserChat, "/collect")).Text);
        Assert.Equal(CollectionSection.AdminOnly, (await Press(UserChat, "cg:")).Notice);
        Assert.Null(factory.Services.GetRequiredService<HhAutoApplier.Application.Commands.Collecting.CollectionProgress>().State.Current);

        var collect = await Screen(OwnerChat, "/collect");
        Assert.Contains("Сбор вакансий", collect.Text);
        Assert.Contains(collect.Buttons, b => b.Data == "cg:");
    }

    [Fact]
    public async Task Vacancies_FilterListCardActions_ForgedDataFindsNothing()
    {
        await CreateReviews(3);
        await Link(_admin, OwnerChat);
        await Json(await _admin.PostAsJsonAsync("/api/vacancies/1/review", new { approve = true }));
        await Json(await _admin.PostAsJsonAsync("/api/vacancies/2/review", new { approve = false }));

        var menu = await Screen(OwnerChat, "/vacancies");
        var queued = Assert.Single(menu.Buttons, b => b.Text.StartsWith("в очереди"));
        var list = (await Press(OwnerChat, queued.Data)).Screen!;
        Assert.Contains("C# &lt;разработчик&gt; 1", list.Text);
        Assert.DoesNotContain("разработчик&gt; 2", list.Text);

        // Фильтр по поиску — по id
        var filter = (await Press(OwnerChat, Assert.Single(list.Buttons, b => b.Data.StartsWith("vf:")).Data)).Screen!;
        var dotnet = Assert.Single(filter.Buttons, b => b.Text == "dotnet");
        Assert.Contains("поиск: dotnet", (await Press(OwnerChat, dotnet.Data)).Screen!.Text);

        // Карточка и действия, как vacancyActions в вебе
        var card = (await Press(OwnerChat, Assert.Single(list.Buttons, b => b.Data.StartsWith("vc:1:")).Data)).Screen!;
        Assert.Contains(card.Buttons, b => b.Url == "https://hh.ru/vacancy/1");
        var description = (await Press(OwnerChat, Assert.Single(card.Buttons, b => b.Data.StartsWith("vd:")).Data)).Screen!;
        Assert.StartsWith("vc:1:", Assert.Single(description.Buttons).Data);

        var skipped = await Press(OwnerChat, Assert.Single(card.Buttons, b => b.Data.StartsWith("vk:")).Data);
        Assert.Equal("Пропущена", skipped.Notice);
        Assert.Equal((VacancyStatus.Skipped, "пропущено вручную"), ((await Application("1")).Status, (await Application("1")).StatusReason));

        var hidden = await Press(OwnerChat, Assert.Single(skipped.Screen!.Buttons, b => b.Data.StartsWith("vh:")).Data);
        Assert.NotNull((await Application("1")).HiddenAt);
        await Press(OwnerChat, Assert.Single(hidden.Screen!.Buttons, b => b.Data.StartsWith("vu:")).Data);
        Assert.Null((await Application("1")).HiddenAt);
        var back = await Press(OwnerChat, Assert.Single(skipped.Screen.Buttons, b => b.Data.StartsWith("vq:")).Data);
        Assert.Equal("В очереди", back.Notice);
        Assert.Equal(VacancyStatus.Queued, (await Application("1")).Status);
        // Возврат к тому же списку
        Assert.Equal(queued.Data, Assert.Single(back.Screen!.Buttons, b => b.Data.StartsWith("vl:")).Data);

        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "vz:2:0.0.0")).Notice);
        Assert.NotNull((await Press(OwnerChat, "ve:2:3.0.0")).Screen);
        Assert.NotEqual(VacancyStatus.Skipped, (await Application("2")).Status);

        // Пропустить с причиной — экран ждёт текст, введённое убирается из чата, экран — снова карточка
        var prompt = (await Press(OwnerChat, Assert.Single(back.Screen.Buttons, b => b.Data.StartsWith("vr:")).Data)).Screen!;
        Assert.Equal("ix:", Assert.Single(prompt.Buttons).Data);
        var typed = await Send(OwnerChat, "далеко ехать", messageId: 77);
        Assert.Equal([77], typed.Delete);
        Assert.False(typed.MoveScreen);
        Assert.Equal(500, typed.ScreenMessageId);
        Assert.Equal("Пропущена", typed.Notice);
        Assert.Equal("пропущено вручную: далеко ехать", (await Application("1")).StatusReason);

        // Чужой чат: подделанная callback_data и ввод в режиме с чужим id не трогают вакансии владельца
        var user = await CreateUser("user@test.local");
        await Link(user, UserChat);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "vc:3:-1.0.0")).Notice);
        Assert.Contains("не найдена", (await Press(UserChat, "vk:3:-1.0.0")).Notice);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "vd:3:r")).Notice);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "vr:3:0.0.0")).Notice);
        await SetInput(UserChat, BotInput.SkipReason, "3", null, DateTimeOffset.UtcNow);
        Assert.Contains("не найдена", (await Send(UserChat, "чужая")).Notice);
        Assert.Equal(VacancyStatus.Review, (await Application("3")).Status);
        Assert.DoesNotContain("разработчик", (await Press(UserChat, "vl:-1.0.0")).Screen!.Text);
    }

    [Fact]
    public async Task Review_EditLetterByInput_DraftKept_ApproveWithDraft()
    {
        await CreateReviews(description: new string('д', 5000));
        await Link(_admin, OwnerChat);

        var card = await Screen(OwnerChat, "/review");
        var edit = Assert.Single(card.Buttons, b => b.Text == "✏️ Письмо");
        var vacancyId = edit.Data.Split(':')[1];

        // Длинное описание — на экране, обрезано со ссылкой на hh.ru, «← Назад» — к карточке
        var description = (await Press(OwnerChat, $"vd:{vacancyId}:r")).Screen!;
        Assert.True(BotHtml.VisibleLength(description.Text) <= BotHtml.MaxText);
        Assert.Contains("на hh.ru", description.Text);
        Assert.Equal($"rk:{vacancyId}", Assert.Single(description.Buttons).Data);

        var prompt = (await Press(OwnerChat, edit.Data)).Screen!;
        Assert.Contains($"<pre>{ReviewSection.NoLetter}</pre>", prompt.Text);
        // «Отменить» — к карточке, режим снят
        Assert.Contains("1 из 2", (await Press(OwnerChat, "ix:")).Screen!.Text);
        Assert.Contains("Текст принимается", (await Send(OwnerChat, "мимо")).Screen!.Text);

        await Press(OwnerChat, edit.Data);
        var typed = await Send(OwnerChat, "Здравствуйте! <Моё> письмо.", messageId: 55);
        Assert.Equal([55], typed.Delete);
        Assert.Contains("есть правка", typed.Screen!.Text);
        Assert.Contains("&lt;Моё&gt;", typed.Screen.Text);
        Assert.Contains(typed.Screen.Buttons, b => b.Data == $"rd:{vacancyId}");
        Assert.Null((await Application(vacancyId)).CoverLetter);

        // Правка живёт в черновике: карточку можно уйти и вернуть, затем поправить ещё раз
        await Press(OwnerChat, "menu:");
        Assert.Contains("есть правка", (await Press(OwnerChat, $"rk:{vacancyId}")).Screen!.Text);
        var again = (await Press(OwnerChat, edit.Data)).Screen!;
        Assert.Contains("&lt;Моё&gt; письмо.</pre>", again.Text);
        await Send(OwnerChat, "Второй вариант письма.");

        // Чужой чат не одобрит по черновику владельца
        await Link(await CreateUser("user@test.local"), UserChat);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, $"rd:{vacancyId}")).Notice);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, $"rl:{vacancyId}")).Notice);
        await SetInput(UserChat, BotInput.Letter, vacancyId, null, DateTimeOffset.UtcNow);
        Assert.Equal("Вакансия не найдена", (await Send(UserChat, "чужое письмо")).Notice);
        Assert.Null((await Application(vacancyId)).CoverLetter);

        var approved = await Press(OwnerChat, $"rd:{vacancyId}");
        Assert.Equal("✅ Одобрено с вашими правками · осталось 1", approved.Notice);
        var saved = await Application(vacancyId);
        Assert.Equal((VacancyStatus.Queued, "Второй вариант письма.", JobApplication.ManualLetter), (saved.Status, saved.CoverLetter, saved.CoverLetterModel));
        Assert.Null(await factory.WithDbAsync(db => db.TelegramChats.Where(c => c.ChatId == OwnerChat).Select(c => c.DraftVacancyId).SingleAsync()));
    }

    [Fact]
    public async Task Review_InputExpires()
    {
        await CreateReviews(1);
        await Link(_admin, OwnerChat);
        await SetInput(OwnerChat, BotInput.Letter, "1", null, DateTimeOffset.UtcNow.AddHours(-1));
        var late = await Send(OwnerChat, "Поздно");
        Assert.Contains("Время ввода вышло", late.Screen!.Text);
        Assert.True(late.MoveScreen);
        Assert.Null(await factory.WithDbAsync(db => db.TelegramChats.Where(c => c.ChatId == OwnerChat).Select(c => c.DraftVacancyId).SingleAsync()));
    }

    [Fact]
    public async Task Review_EditQuestionnaireAnswer_DeclineWithReason()
    {
        await CreateReviews();
        await Link(_admin, OwnerChat);
        object Questions = new
        {
            questions = new object[]
            {
                new { question = "Какой принцип SOLID про абстракции?", type = "Radio", options = new[] { "SRP", "DIP" } },
                new { question = "Почему мы?", type = "Text" },
            },
        };
        // Анкету расширение присылает для отклика в очереди
        await Json(await _admin.PostAsJsonAsync("/api/vacancies/1/review", new { approve = true }));
        await Json(await _admin.PostAsJsonAsync("/api/vacancies/1/questionnaire", Questions));
        factory.Llm.Answers.Enqueue("""{"answers": ["DIP", "интересный продукт"]}""");
        await Json(await _admin.PostAsJsonAsync("/api/llm/answers", new { limit = 10 }));
        Assert.Equal(VacancyStatus.Review, (await Application("1")).Status);

        var card = (await Press(OwnerChat, "rk:1")).Screen!;
        Assert.Contains("интересный продукт", card.Text);
        var answer = Assert.Single(card.Buttons, b => b.Text == "✏️ Ответ 1");
        Assert.Contains("<pre>интересный продукт</pre>", (await Press(OwnerChat, answer.Data)).Screen!.Text);

        var typed = (await Send(OwnerChat, "команда и стек")).Screen!;
        Assert.Contains("ответ 1, ✏️ поправлен", typed.Text);
        await Press(OwnerChat, Assert.Single(typed.Buttons, b => b.Data.StartsWith("rd:")).Data);
        var saved = await Application("1");
        Assert.Equal(VacancyStatus.Queued, saved.Status);
        Assert.Equal(["DIP", "команда и стек"], saved.Questions.Select(q => string.Join("|", q.Answer)));

        // Отклонить с причиной — режимом ввода
        await Press(OwnerChat, "rr:2");
        var declined = await Send(OwnerChat, "не мой стек");
        Assert.Equal("❌ Отклонено · проверка пуста", declined.Notice);
        Assert.Equal((VacancyStatus.Skipped, "отклонено вручную: не мой стек"), ((await Application("2")).Status, (await Application("2")).StatusReason));
        Assert.StartsWith("Уже решено", (await Press(OwnerChat, "rr:2")).Notice);
    }

    [Fact]
    public async Task Searches_ToggleOwnOnly()
    {
        await Link(_admin, OwnerChat);
        var search = await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#", rules = new { minSalaryRub = 200_000 } }));
        var id = search.GetProperty("id").GetInt32();

        var screen = await Screen(OwnerChat, "/searches");
        Assert.Contains("✅", screen.Text);
        var toggled = await Press(OwnerChat, Assert.Single(screen.Buttons, b => b.Data == $"st:{id}").Data);
        Assert.Equal("Выключен", toggled.Notice);
        var saved = (await Json(await _admin.GetAsync("/api/searches"))).EnumerateArray().Single();
        Assert.False(saved.GetProperty("enabled").GetBoolean());
        // Правила не потерялись
        Assert.Equal(200_000, saved.GetProperty("rules").GetProperty("minSalaryRub").GetInt32());

        await Link(await CreateUser("user@test.local"), UserChat);
        Assert.Equal("Поиск не найден", (await Press(UserChat, $"st:{id}")).Notice);
        Assert.False((await Json(await _admin.GetAsync("/api/searches"))).EnumerateArray().Single().GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Dialogs_ListCardDraftHandled_OwnOnly()
    {
        await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#" }));
        object Card(string id) => new { id, title = $"C# {id}", company = "Acme", description = "", salaryCurrency = "RUR", workFormat = "Удалённо" };
        await Json(await _admin.PostAsJsonAsync("/api/import", new { search = "dotnet", vacancies = new[] { Card("1"), Card("2") } }));
        await factory.ConfirmImportedAsync();
        foreach (var id in new[] { "1", "2" })
            await Json(await _admin.PostAsJsonAsync($"/api/vacancies/{id}/status", new { status = "Applied" }));
        await Link(_admin, OwnerChat);

        var at = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        object Chat(long chatId, string vacancyId, string state, string text) =>
            new { chatId, vacancyId, title = $"C# {vacancyId}", company = "Acme", state, messageId = chatId * 10, text, at, fromEmployer = true };
        await Json(await _admin.PostAsJsonAsync("/api/dialogs", new { chats = new[] { Chat(21, "1", "INTERVIEW", "Приглашаем на звонок"), Chat(22, "2", "DISCARD", "Отказ") } }));
        factory.Llm.Configured = true;
        factory.Llm.Answers.Enqueue("""{"kind": "invitation", "summary": "звонок завтра", "reply": "Спасибо! Удобно [время]."}""");
        await InScope(sp => sp.GetRequiredService<HhAutoApplier.Application.Commands.Dialogs.AnalyzeDialogsCommandHandler>()
            .Handle(new HhAutoApplier.Application.Commands.Dialogs.AnalyzeDialogsCommand()));

        // Необработанные — без отказов
        var list = await Screen(OwnerChat, "/dialogs");
        Assert.Contains("звонок завтра", list.Text);
        Assert.DoesNotContain("Отказ", list.Text);
        Assert.Contains("Отказ", (await Press(OwnerChat, "dl:0:a")).Screen!.Text);

        var card = (await Press(OwnerChat, Assert.Single(list.Buttons, b => b.Data.StartsWith("dc:21")).Data)).Screen!;
        Assert.Contains("<pre>Спасибо! Удобно [время].</pre>", card.Text);
        Assert.Contains(card.Buttons, b => b.Url == "https://hh.ru/chat/21");

        // Чужой чат: карточки и «Обработано» нет
        await Link(await CreateUser("user@test.local"), UserChat);
        Assert.Equal("Чат не найден", (await Press(UserChat, "dc:21:0:o")).Notice);
        Assert.Equal("Чат не найден", (await Press(UserChat, "dh:21:0:o")).Notice);
        Assert.Equal("Чат не найден", (await Press(UserChat, "handled:21")).Notice);

        var handled = await Press(OwnerChat, Assert.Single(card.Buttons, b => b.Data.StartsWith("dh:")).Data);
        Assert.Equal("Обработано", handled.Notice);
        Assert.DoesNotContain(handled.Screen!.Buttons, b => b.Data.StartsWith("dh:"));
        Assert.Contains("Новых ответов работодателей нет", (await Screen(OwnerChat, "/dialogs")).Text);
    }

    [Fact]
    public void Fit_LongTextPlainAndCut_EscapesMarkup()
    {
        Assert.Equal("a &lt;b&gt; &amp; c", BotHtml.E("a <b> & c"));
        Assert.Equal("x", BotHtml.Link("x", "javascript:alert(1)"));
        var long_ = new BotMessage($"<b>{new string('я', 5000)}</b>") { Html = true };
        var fitted = BotHtml.Fit(long_);
        Assert.False(fitted.Html);
        Assert.True(fitted.Text.Length <= BotHtml.MaxText);
        Assert.Contains("обрезано", fitted.Text);
        var small = new BotMessage("<b>ok</b>") { Html = true };
        Assert.Same(small, BotHtml.Fit(small));
    }

    // Сводка обычных уведомлений (#115): дописывается, пока свежая, помещается и не открыта
    [Fact]
    public void Digest_AppendsWhileFresh_ResetOnOpen()
    {
        var chat = HhAutoApplier.Domain.Telegram.TelegramChat.Create(Guid.NewGuid());
        var at = DateTimeOffset.UtcNow;
        Assert.Null(chat.AppendToDigest("первая", at));
        chat.ShowDigest(7, "🔔 Новое\nпервая", at, started: true);
        Assert.Equal("🔔 Новое\nпервая\nвторая", chat.AppendToDigest("вторая", at.AddMinutes(5)));
        Assert.Null(chat.AppendToDigest("поздняя", at + HhAutoApplier.Domain.Telegram.TelegramChat.DigestLifetime + TimeSpan.FromMinutes(1)));
        Assert.Null(chat.AppendToDigest(new string('x', 4000), at));
        chat.ResetDigest();
        Assert.Null(chat.AppendToDigest("после открытия", at));
    }
}
