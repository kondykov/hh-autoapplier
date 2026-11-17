using System.Text;
using HhAutoApplier.Application.Commands.Vacancies;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.Searches;
using HhAutoApplier.Application.Queries.Vacancies;
using HhAutoApplier.Domain.Vacancies;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// «Вакансии», как в вебе: фильтр по статусу и поиску, список с листанием на месте, карточка с действиями vacancyActions.
// Контекст списка — «статус.сдвиг.поиск» (статус -1 — все, поиск 0 — все) едет в кнопках карточки: «← К списку» возвращает туда же
public class VacanciesSection(
    GetVacanciesQueryHandler vacancies,
    GetVacancyQueryHandler vacancy,
    GetStatsQueryHandler stats,
    GetSearchesQueryHandler searches,
    SetVacancyStatusCommandHandler setStatus,
    SetVacancyHiddenCommandHandler setHidden,
    ReevaluateVacancyCommandHandler reevaluate,
    BotChat chat
) : IBotSection
{
    public const string Menu = "vm";

    public const string List = "vl";

    public const string Filter = "vf";

    public const string Card = "vc";

    public const string Requeue = "vq";

    public const string Reevaluate = "ve";

    public const string Hide = "vh";

    public const string Unhide = "vu";

    public const string Skip = "vk";

    public const string SkipWithReason = "vr";

    public const string Description = "vd";

    public const int PageSize = 8;

    private const int All = -1;

    // Решения вне проверки: пропущенную или упавшую — в очередь, ожидающую — пропустить (как REQUEUE и SKIPPABLE в вебе)
    private static readonly VacancyStatus[] Requeueable = [VacancyStatus.Skipped, VacancyStatus.Failed];

    private static readonly VacancyStatus[] Skippable = [VacancyStatus.Queued, VacancyStatus.Pending, VacancyStatus.Questionnaire, VacancyStatus.New];

    public IReadOnlyCollection<string> Actions => [Menu, List, Filter, Card, Requeue, Reevaluate, Hide, Unhide, Skip, SkipWithReason, Description];

    public static string Context(int status, int skip, int search) => $"{status}.{skip}.{search}";

    private record ListContext(int Status, int Skip, int Search)
    {
        public override string ToString() => Context(Status, Skip, Search);

        public static ListContext Parse(string text)
        {
            var parts = text.Split('.');
            int At(int i, int fallback) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : fallback;
            return new ListContext(At(0, All), Math.Max(At(1, 0), 0), Math.Max(At(2, 0), 0));
        }
    }

    public async Task<BotResult> PressAsync(BotPress press, CancellationToken ct)
    {
        var id = press.Arg(0);
        switch (press.Action)
        {
            case Menu:
                return BotResults.Show(await MenuAsync(ct));
            case List:
                return BotResults.Show(await ListAsync(ListContext.Parse(press.Arg(0)), ct));
            case Filter:
                return BotResults.Show(await FilterAsync(ListContext.Parse(press.Arg(0)), ct));
            case Card:
                return await CardResultAsync(id, ListContext.Parse(press.Arg(1)), "", ct);
            case Description:
                return await DescriptionAsync(id, press.Arg(1), ct);
            case SkipWithReason:
                return await SkipPromptAsync(id, press.Arg(1), ct);
        }

        var context = ListContext.Parse(press.Arg(1));
        var (error, done) = press.Action switch
        {
            Requeue => Outcome((await setStatus.Handle(new SetVacancyStatusCommand { VacancyId = id, Status = VacancyStatus.Queued }, ct)).Value),
            Skip => Outcome((await setStatus.Handle(new SetVacancyStatusCommand { VacancyId = id, Status = VacancyStatus.Skipped, Reason = SkipReason(null) }, ct)).Value),
            Hide => Outcome((await setHidden.Handle(new SetVacancyHiddenCommand { VacancyId = id, Hidden = true }, ct)).Value),
            Unhide => Outcome((await setHidden.Handle(new SetVacancyHiddenCommand { VacancyId = id, Hidden = false }, ct)).Value),
            Reevaluate => Outcome((await reevaluate.Handle(new ReevaluateVacancyCommand { VacancyId = id }, ct)).Value),
            _ => ("Неизвестная кнопка", null),
        };
        if (done is null)
            return BotResults.Notice(Cut(error, 190));

        var notice = press.Action switch
        {
            Requeue => done.Status == VacancyStatus.Review ? "На проверке: есть просьбы к письму" : "В очереди",
            Skip => "Пропущена",
            Hide => "Скрыта",
            Unhide => "Возвращена в список",
            _ => done.Status == VacancyStatus.Skipped ? "Снова отсеяна" : Status(done.Status),
        };
        return BotResults.Show(CardScreen(done, context), notice);
    }

    // Итог команды: VacancyDto или текст ошибки (не найдена, переход недопустим, LLM недоступна)
    private static (string Error, VacancyDto? Done) Outcome(object value) =>
        value is VacancyDto dto ? ("", dto) : (value is Error e ? e.Message : "Не удалось", null);

    // Как в вебе: причина пропуска вручную — в отчёте и истории
    public static string SkipReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? "пропущено вручную" : $"пропущено вручную: {Cut(reason, 400)}";

    // Выбор статуса: кнопки со счётчиками, как плитки обзора
    public async Task<BotMessage> MenuAsync(CancellationToken ct)
    {
        var counts = (await stats.Handle(new GetStatsQuery(), ct)).Value is Dictionary<string, int> d ? d : [];
        var buttons = Enum.GetValues<VacancyStatus>().Where(s => counts.GetValueOrDefault(s.ToString()) > 0)
            .Select(s => new BotButton($"{Status(s)}: {counts[s.ToString()]}", $"{List}:{Context((int)s, 0, 0)}"));
        var rows = Rows(buttons, 2).ToList();
        rows.Add([new BotButton($"Все: {counts.Values.Sum()}", $"{List}:{Context(All, 0, 0)}"), new BotButton("☰ Меню", $"{MenuSection.Show}:")]);
        return new BotMessage($"📋 {B("Вакансии")}: выберите статус") { Html = true, Rows = rows };
    }

    private async Task<BotMessage> ListAsync(ListContext context, CancellationToken ct)
    {
        var search = await SearchNameAsync(context.Search, ct);
        var query = new GetVacanciesQuery
        {
            Status = context.Status == All ? null : (VacancyStatus)context.Status,
            Search = search,
            Skip = context.Skip,
            Take = PageSize + 1,
        };
        var items = (await vacancies.Handle(query, ct)).Value is List<VacancyDto> list ? list : [];
        var page = items.Take(PageSize).ToList();

        var title = context.Status == All ? "все статусы" : Status((VacancyStatus)context.Status);
        var text = new StringBuilder($"📋 {B("Вакансии")} · {E(title)} · поиск: {E(search ?? "все")}");
        if (page.Count == 0)
            text.Append("\n\nНичего нет.");
        else
        {
            text.Append($" · {context.Skip + 1}–{context.Skip + page.Count}\n");
            foreach (var (v, i) in page.Select((v, i) => (v, i)))
            {
                var meta = new[] { v.Company, context.Status == All ? Status(v.Status) : "", $"балл {v.LlmScore ?? v.Score}", v.HiddenAt is null ? "" : "скрыта" };
                text.Append($"\n{context.Skip + i + 1}. {B(Cut(v.Title, 80))} — {E(string.Join(" · ", meta.Where(m => !string.IsNullOrWhiteSpace(m))))}");
            }
        }

        var rows = page.Select((v, i) => (IReadOnlyList<BotButton>)[new BotButton($"{context.Skip + i + 1}. {Cut(v.Title, 40)}", $"{Card}:{v.Id}:{context}")]).ToList();
        var nav = new List<BotButton>();
        if (context.Skip > 0)
            nav.Add(new BotButton("← Назад", $"{List}:{context with { Skip = Math.Max(0, context.Skip - PageSize) }}"));
        if (items.Count > PageSize)
            nav.Add(new BotButton("Дальше →", $"{List}:{context with { Skip = context.Skip + PageSize }}"));
        if (nav.Count > 0)
            rows.Add(nav);
        rows.Add([new BotButton("🔎 Поиск", $"{Filter}:{context}"), new BotButton("☰ Статусы", $"{Menu}:"), new BotButton("☰ Меню", $"{MenuSection.Show}:")]);
        return new BotMessage(text.ToString()) { Html = true, Rows = rows };
    }

    private async Task<BotMessage> FilterAsync(ListContext context, CancellationToken ct)
    {
        var list = (await searches.Handle(new GetSearchesQuery(), ct)).Value is List<SearchDto> s ? s : [];
        var rows = Rows(list.Select(s => new BotButton(Cut(s.Name, 40), $"{List}:{context with { Skip = 0, Search = s.Id }}")), 2).ToList();
        rows.Add([new BotButton("Все поиски", $"{List}:{context with { Skip = 0, Search = 0 }}")]);
        return new BotMessage($"🔎 {B("Вакансии")}: выберите поиск") { Html = true, Rows = rows };
    }

    // Фильтр по поиску — по id в кнопке; GetVacanciesQuery ищет по имени. Чужой или удалённый id — «все»
    private async Task<string?> SearchNameAsync(int id, CancellationToken ct)
    {
        if (id == 0)
            return null;

        var list = (await searches.Handle(new GetSearchesQuery(), ct)).Value is List<SearchDto> s ? s : [];
        return list.FirstOrDefault(s => s.Id == id)?.Name;
    }

    private async Task<BotResult> CardResultAsync(string id, ListContext context, string notice, CancellationToken ct) =>
        (await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is VacancyDto v
            ? BotResults.Show(CardScreen(v, context), notice)
            : BotResults.Notice("Вакансия не найдена");

    private static BotMessage CardScreen(VacancyDto v, ListContext context)
    {
        var rows = new List<IReadOnlyList<BotButton>>();
        var actions = new List<BotButton>();
        if (v.Status == VacancyStatus.Review)
            actions.Add(new BotButton("📝 Решить", $"{BotTexts.ReviewCard}:{v.Id}"));
        else if (v.HiddenAt is not null)
            actions.Add(new BotButton("↩️ Вернуть в список", $"{Unhide}:{v.Id}:{context}"));
        else if (Requeueable.Contains(v.Status))
        {
            actions.Add(new BotButton("📥 В очередь", $"{Requeue}:{v.Id}:{context}"));
            // Оценить заново и скрыть — только отсеянные: у Failed отклик мог уйти на hh.ru
            if (v.Dismissed)
            {
                actions.Add(new BotButton("🔁 Оценить заново", $"{Reevaluate}:{v.Id}:{context}"));
                actions.Add(new BotButton("🙈 Скрыть", $"{Hide}:{v.Id}:{context}"));
            }
        }
        else if (Skippable.Contains(v.Status))
        {
            actions.Add(new BotButton("⏭ Пропустить", $"{Skip}:{v.Id}:{context}"));
            actions.Add(new BotButton("⏭ С причиной", $"{SkipWithReason}:{v.Id}:{context}"));
        }

        rows.AddRange(Rows(actions, 3));
        var links = new List<BotButton> { new("📄 Описание", $"{Description}:{v.Id}:{context}") };
        if (SafeUrl(v.Url) is { } url)
            links.Add(BotButton.Link("🔗 hh.ru", url));
        rows.Add(links);
        rows.Add([new BotButton("← К списку", $"{List}:{context}")]);
        return new BotMessage(CardText(v)) { Html = true, Rows = rows };
    }

    // Карточка как vacancyCard в вебе; описание — отдельным сообщением по кнопке
    public static string CardText(VacancyDto v)
    {
        var pills = new[] { Status(v.Status), v.HiddenAt is null ? "" : "скрыта", v.VacancyArchived ? "в архиве" : "" };
        var meta = new[]
        {
            v.Company, v.Area, Salary(v), v.WorkFormat, v.WorkExperience is { } exp ? $"опыт: {exp}" : "", $"балл {v.LlmScore ?? v.Score}",
            v.ResumeProfile is { } r ? $"резюме {r}" : "", v.SearchName is { Length: > 0 } s ? $"поиск {s}" : "", v.AppliedAt is { } at ? $"отклик {Date(at)}" : "",
        };
        var text = new StringBuilder()
            .Append($"<b>{Link(v.Title, v.Url)}</b>\n")
            .Append(I(Join(pills))).Append('\n')
            .Append(E(Join(meta))).Append('\n')
            .Append(E($"найдена {Date(v.CollectedAt)} · встречалась {Date(v.LastSeenAt)}"));
        if ((v.StatusReason ?? v.LlmReason) is { } reason)
            text.Append($"\n\n{I(Cut(reason, 500))}");
        text.Append(ReviewSection.RequestsText(v));
        if (!string.IsNullOrWhiteSpace(v.CoverLetter))
            text.Append($"\n\n{B("Письмо")}\n{Quote(Cut(v.CoverLetter, 1500))}");
        if (v.History.Count > 0)
        {
            text.Append($"\n\n{B($"История ({v.History.Count})")}");
            foreach (var h in v.History.TakeLast(5))
                text.Append($"\n{E($"{Date(h.At)} → {Status(h.To)}{(string.IsNullOrWhiteSpace(h.Reason) ? "" : $": {Cut(h.Reason, 120)}")}")}");
        }

        return text.ToString();
    }

    private static string Join(IEnumerable<string?> parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    // Описание вакансии — на экране вместо карточки, «← Назад» возвращает к ней (r — к карточке проверки). Длинное — обрезано, полностью — на hh.ru
    public async Task<BotResult> DescriptionAsync(string id, string back, CancellationToken ct)
    {
        if ((await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is not VacancyDto v)
            return BotResults.Notice("Вакансия не найдена");

        var text = new StringBuilder($"📄 <b>{Link(v.Title, v.Url)}</b>\n");
        if (v.Stack.Length > 0)
            text.Append(E($"Стек: {string.Join(", ", v.Stack)}")).Append('\n');
        text.Append('\n').Append(E(Cut(string.IsNullOrWhiteSpace(v.Description) ? "описания нет" : v.Description, 3500)));
        if (v.Description.Length > 3500)
            text.Append($"\n\nПолностью — {Link("на hh.ru", v.Url)}");
        var backData = back == "r" ? $"{BotTexts.ReviewCard}:{v.Id}" : $"{Card}:{v.Id}:{ListContext.Parse(back)}";
        return BotResults.Show(new BotMessage(text.ToString()) { Html = true, Rows = [[new BotButton("← Назад", backData)]] });
    }

    // Пропустить с причиной: экран ждёт причину обычным сообщением
    private async Task<BotResult> SkipPromptAsync(string id, string context, CancellationToken ct)
    {
        if ((await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is not VacancyDto v)
            return BotResults.Notice("Вакансия не найдена");

        await chat.AwaitInputAsync(BotInput.SkipReason, v.Id, null, $"{Card}:{v.Id}:{ListContext.Parse(context)}", ct);
        return BotResults.Show(BotInput.Screen($"⏭ {B("Причина пропуска")}\n{B(v.Title)}\n\nНапишите причину обычным сообщением — вакансия уйдёт в «пропущенные»."));
    }

    // Причина пропуска из режима ввода; back — карточка, откуда пришли
    public async Task<BotResult> SkipAsync(string id, string reason, string back, CancellationToken ct)
    {
        var context = ListContext.Parse(BotPress.Parse(back).Arg(1));
        var result = (await setStatus.Handle(new SetVacancyStatusCommand { VacancyId = id, Status = VacancyStatus.Skipped, Reason = SkipReason(reason) }, ct)).Value;
        return result is VacancyDto v
            ? BotResults.Show(CardScreen(v, context), "Пропущена")
            : BotResults.Show(await MenuAsync(ct), Cut(result is Error e ? e.Message : "Не удалось", 190));
    }
}
