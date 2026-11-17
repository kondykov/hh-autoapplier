using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HhAutoApplier.Application.Commands.Telegram;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Tests.Api;

// Разделы бота (#115): обзор, вакансии, проверка с правкой письма ответом на сообщение, ответы работодателей, поиски, сбор.
// Транспорт Telegram не участвует: сообщения бота, на которые отвечают или под которыми жмут, подаются как BotSource —
// текст без разметки и блоки <pre>, как их видит TelegramHostedService
[Collection(ApiCollection.Name)]
public partial class TelegramSectionsTests(ApiFactory factory) : IAsyncLifetime
{
    private const long OwnerChat = 100;

    private const long UserChat = 200;

    private readonly HttpClient _admin = factory.CreateApiClient();

    // Кнопки всех экранов теста — для проверки лимита callback_data
    private readonly List<BotButton> _seen = [];

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

    private async Task<IReadOnlyList<BotMessage>> Send(long chat, string text, BotSource? replyTo = null)
    {
        var replies = await InScope(sp => sp.GetRequiredService<HandleTelegramMessageCommandHandler>()
            .Handle(new HandleTelegramMessageCommand { ChatId = chat, IsPrivate = true, Text = text, Username = "owner", ReplyTo = replyTo }));
        _seen.AddRange(replies.SelectMany(r => r.Buttons));
        return replies;
    }

    private async Task<BotCallbackResult> Press(long chat, string data, BotSource? source = null)
    {
        var result = await InScope(sp => sp.GetRequiredService<HandleTelegramCallbackCommandHandler>()
            .Handle(new HandleTelegramCallbackCommand { ChatId = chat, Data = data, Source = source }));
        _seen.AddRange(result.Edit?.Buttons ?? []);
        _seen.AddRange(result.Replies.SelectMany(r => r.Buttons));
        _seen.AddRange(result.StatusRows.SelectMany(r => r));
        return result;
    }

    // Сообщение бота глазами транспорта: HTML разобран в текст, содержимое <pre> — блоками
    private static BotSource AsSent(BotMessage message, bool fromBot = true)
    {
        var blocks = PreBlocks().Matches(message.Text).Select(m => BotHtml.Plain(m.Groups[1].Value)).ToList();
        return new BotSource(message.Html ? BotHtml.Plain(message.Text) : message.Text, blocks, fromBot);
    }

    [GeneratedRegex("<pre>(.*?)</pre>", RegexOptions.Singleline)]
    private static partial Regex PreBlocks();

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

    [Fact]
    public async Task Overview_CountsButtonsAndCsv_CollectOnlyForAdmin()
    {
        await CreateReviews();
        await Link(_admin, OwnerChat);
        var user = await CreateUser("user@test.local");
        await Link(user, UserChat);

        var overview = Assert.Single(await Send(OwnerChat, BotTexts.OverviewButton));
        Assert.True(overview.Html);
        // Название из вакансии экранировано: «<» сломал бы разметку Telegram
        Assert.DoesNotContain("<разработчик>", overview.Text);
        Assert.Contains(overview.Buttons, b => b.Data == $"{VacanciesSection.List}:{(int)VacancyStatus.Review}.0.0");
        Assert.Contains(overview.Buttons, b => b.Data == "cs:");

        var report = Assert.Single((await Press(OwnerChat, "csv:")).Replies);
        Assert.Equal("applications.csv", report.File!.Name);
        Assert.StartsWith("Вакансия;", Encoding.UTF8.GetString(report.File.Content).TrimStart('﻿'));

        // Пользователь видит только свои счётчики, сбора у него нет
        var own = Assert.Single(await Send(UserChat, "/overview"));
        Assert.Contains("Откликов пока нет", own.Text);
        Assert.DoesNotContain(own.Buttons, b => b.Data == "cs:");
        Assert.Equal(CollectionSection.AdminOnly, Assert.Single(await Send(UserChat, "/collect")).Text);
        Assert.Equal(CollectionSection.AdminOnly, (await Press(UserChat, "cg:")).Notice);
        Assert.Null(factory.Services.GetRequiredService<HhAutoApplier.Application.Commands.Collecting.CollectionProgress>().State.Current);

        var collect = Assert.Single(await Send(OwnerChat, "/collect"));
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

        var menu = Assert.Single(await Send(OwnerChat, BotTexts.VacanciesButton));
        var queued = Assert.Single(menu.Buttons, b => b.Text.StartsWith("в очереди"));
        var list = (await Press(OwnerChat, queued.Data)).Edit!;
        Assert.Contains("C# &lt;разработчик&gt; 1", list.Text);
        Assert.DoesNotContain("разработчик&gt; 2", list.Text);

        // Фильтр по поиску — по id
        var filter = (await Press(OwnerChat, Assert.Single(list.Buttons, b => b.Data.StartsWith("vf:")).Data)).Edit!;
        var dotnet = Assert.Single(filter.Buttons, b => b.Text == "dotnet");
        Assert.Contains("поиск: dotnet", (await Press(OwnerChat, dotnet.Data)).Edit!.Text);

        // Карточка и действия, как vacancyActions в вебе
        var card = (await Press(OwnerChat, Assert.Single(list.Buttons, b => b.Data.StartsWith("vc:1:")).Data)).Edit!;
        Assert.Contains(card.Buttons, b => b.Url == "https://hh.ru/vacancy/1");
        var skip = Assert.Single(card.Buttons, b => b.Data.StartsWith("vk:"));
        var skipped = await Press(OwnerChat, skip.Data);
        Assert.Equal("Пропущена", skipped.Notice);
        Assert.Equal(VacancyStatus.Skipped, (await Application("1")).Status);
        Assert.Equal("пропущено вручную", (await Application("1")).StatusReason);

        var hide = Assert.Single(skipped.Edit!.Buttons, b => b.Data.StartsWith("vh:"));
        var hidden = await Press(OwnerChat, hide.Data);
        Assert.NotNull((await Application("1")).HiddenAt);
        await Press(OwnerChat, Assert.Single(hidden.Edit!.Buttons, b => b.Data.StartsWith("vu:")).Data);
        Assert.Null((await Application("1")).HiddenAt);
        var back = (await Press(OwnerChat, Assert.Single(skipped.Edit.Buttons, b => b.Data.StartsWith("vq:")).Data));
        Assert.Equal("В очереди", back.Notice);
        Assert.Equal(VacancyStatus.Queued, (await Application("1")).Status);
        // Возврат к тому же списку
        Assert.Equal(queued.Data, Assert.Single(back.Edit!.Buttons, b => b.Data.StartsWith("vl:")).Data);

        Assert.Equal("Неизвестная кнопка", (await Press(OwnerChat, "vz:2:0.0.0")).Notice);
        var reevaluated = await Press(OwnerChat, "ve:2:3.0.0");
        Assert.NotEqual(VacancyStatus.Skipped, (await Application("2")).Status);
        Assert.NotNull(reevaluated.Edit);

        // Пропустить с причиной — ответом на просьбу бота
        var prompt = Assert.Single((await Press(OwnerChat, "vr:1")).Replies);
        Assert.Equal("Причина пропуска", prompt.AskReply);
        Assert.Contains("пропущена", Assert.Single(await Send(OwnerChat, "далеко ехать", AsSent(prompt))).Text);
        Assert.Equal("пропущено вручную: далеко ехать", (await Application("1")).StatusReason);

        // Чужой чат: подделанная callback_data и ответ на просьбу бота не трогают вакансии владельца
        var user = await CreateUser("user@test.local");
        await Link(user, UserChat);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "vc:3:-1.0.0")).Notice);
        Assert.Contains("не найдена", (await Press(UserChat, "vk:3:-1.0.0")).Notice);
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, "vd:3")).Notice);
        Assert.Equal("Вакансия 3 не найдена", Assert.Single(await Send(UserChat, "чужая", AsSent(prompt with { Text = prompt.Text.Replace("#1", "#3") }))).Text);
        Assert.Equal(VacancyStatus.Review, (await Application("3")).Status);
        Assert.DoesNotContain("разработчик", (await Press(UserChat, $"vl:-1.0.0")).Edit!.Text);
    }

    [Fact]
    public async Task Review_EditLetterByReply_DraftCarriesEdits_ApproveWithDraft()
    {
        await CreateReviews(description: new string('д', 5000));
        await Link(_admin, OwnerChat);

        var card = Assert.Single(await Send(OwnerChat, "/review"));
        var edit = Assert.Single(card.Buttons, b => b.Text == "✏️ Письмо");
        var vacancyId = edit.Data.Split(':')[1];

        // Длинное описание — отдельным сообщением, обрезано со ссылкой на hh.ru
        var description = Assert.Single((await Press(OwnerChat, $"vd:{vacancyId}")).Replies);
        Assert.True(BotHtml.VisibleLength(description.Text) <= BotHtml.MaxText);
        Assert.Contains("на hh.ru", description.Text);

        var prompt = Assert.Single((await Press(OwnerChat, edit.Data)).Replies);
        Assert.Equal("Новое письмо", prompt.AskReply);
        Assert.Equal([ReviewSection.NoLetter], AsSent(prompt).Blocks);

        // Ответ не на сообщение бота — не правка, а обычный текст
        Assert.Contains("/review", Assert.Single(await Send(OwnerChat, "Моё письмо", AsSent(prompt, fromBot: false))).Text);
        Assert.Null((await Application(vacancyId)).CoverLetter);

        var draft = Assert.Single(await Send(OwnerChat, "Здравствуйте! <Моё> письмо.", AsSent(prompt)));
        Assert.Equal(["Здравствуйте! <Моё> письмо."], AsSent(draft).Blocks);
        var approve = Assert.Single(draft.Buttons, b => b.Data.StartsWith("rd:"));

        // Повторная правка — ответом на черновик: прежние правки едут в нём
        var second = Assert.Single(await Send(OwnerChat, "Второй вариант письма.", AsSent(draft)));
        Assert.Equal(["Второй вариант письма."], AsSent(second).Blocks);
        Assert.Null((await Application(vacancyId)).CoverLetter);

        // «Одобрить с этим письмом» — только из черновика: с карточки нечего брать
        Assert.Equal("Черновик устарел — откройте карточку заново", (await Press(OwnerChat, approve.Data, AsSent(card))).Notice);
        var approved = await Press(OwnerChat, approve.Data, AsSent(second));
        Assert.Equal("✅ Одобрено с вашими правками — в очереди", approved.Status);
        var saved = await Application(vacancyId);
        Assert.Equal((VacancyStatus.Queued, "Второй вариант письма.", JobApplication.ManualLetter), (saved.Status, saved.CoverLetter, saved.CoverLetterModel));

        // Чужой чат не одобрит по черновику владельца
        await Link(await CreateUser("user@test.local"), UserChat);
        var other = (await Application("1")).Status == VacancyStatus.Review ? "1" : "2";
        var forged = AsSent(second) with { Text = AsSent(second).Text.Replace($"#{vacancyId}", $"#{other}") };
        Assert.Equal("Вакансия не найдена", (await Press(UserChat, $"rd:{other}", forged)).Notice);
        Assert.Equal("Вакансия не найдена", Assert.Single(await Send(UserChat, "чужое письмо", forged)).Text);
        Assert.Equal(VacancyStatus.Review, (await Application(other)).Status);
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

        var card = (await Press(OwnerChat, "rk:1")).Edit!;
        Assert.Contains("интересный продукт", card.Text);
        var answer = Assert.Single(card.Buttons, b => b.Text == "✏️ Ответ 1");
        var prompt = Assert.Single((await Press(OwnerChat, answer.Data)).Replies);
        Assert.Equal("Новый ответ 1", prompt.AskReply);

        var draft = Assert.Single(await Send(OwnerChat, "команда и стек", AsSent(prompt)));
        var blocks = AsSent(draft).Blocks;
        Assert.Equal("команда и стек", blocks[1]);
        Assert.Equal((await Application("1")).CoverLetter ?? ReviewSection.NoLetter, blocks[0]);

        await Press(OwnerChat, Assert.Single(draft.Buttons, b => b.Data.StartsWith("rd:")).Data, AsSent(draft));
        var saved = await Application("1");
        Assert.Equal(VacancyStatus.Queued, saved.Status);
        Assert.Equal(["DIP", "команда и стек"], saved.Questions.Select(q => string.Join("|", q.Answer)));

        // Отклонить с причиной — ответом на просьбу бота
        var reason = Assert.Single((await Press(OwnerChat, "rr:2")).Replies);
        Assert.Equal("Причина отказа", reason.AskReply);
        Assert.Contains("отклонено", Assert.Single(await Send(OwnerChat, "не мой стек", AsSent(reason))).Text);
        Assert.Equal((VacancyStatus.Skipped, "отклонено вручную: не мой стек"), ((await Application("2")).Status, (await Application("2")).StatusReason));
        Assert.StartsWith("Уже решено", Assert.Single(await Send(OwnerChat, "ещё раз", AsSent(reason))).Text);
    }

    [Fact]
    public async Task Searches_ToggleOwnOnly()
    {
        await Link(_admin, OwnerChat);
        var search = await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#", rules = new { minSalaryRub = 200_000 } }));
        var id = search.GetProperty("id").GetInt32();

        var screen = Assert.Single(await Send(OwnerChat, BotTexts.SearchesButton));
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
        var list = Assert.Single(await Send(OwnerChat, BotTexts.DialogsButton));
        Assert.Contains("звонок завтра", list.Text);
        Assert.DoesNotContain("Отказ", list.Text);
        Assert.Contains("Отказ", (await Press(OwnerChat, "dl:0:a")).Edit!.Text);

        var card = (await Press(OwnerChat, Assert.Single(list.Buttons, b => b.Data.StartsWith("dc:21")).Data)).Edit!;
        Assert.Contains("<pre>Спасибо! Удобно [время].</pre>", card.Text);
        Assert.Contains(card.Buttons, b => b.Url == "https://hh.ru/chat/21");

        // Чужой чат: карточки и «Обработано» нет
        await Link(await CreateUser("user@test.local"), UserChat);
        Assert.Equal("Чат не найден", (await Press(UserChat, "dc:21:0:o")).Notice);
        Assert.Equal("Чат не найден", (await Press(UserChat, "dh:21:0:o")).Notice);
        Assert.Equal("Чат не найден", (await Press(UserChat, "handled:21")).Notice);

        var handled = await Press(OwnerChat, Assert.Single(card.Buttons, b => b.Data.StartsWith("dh:")).Data);
        Assert.Equal("Обработано", handled.Notice);
        Assert.DoesNotContain(handled.Edit!.Buttons, b => b.Data.StartsWith("dh:"));
        Assert.Contains("Новых ответов работодателей нет", Assert.Single(await Send(OwnerChat, "/dialogs")).Text);
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
}
