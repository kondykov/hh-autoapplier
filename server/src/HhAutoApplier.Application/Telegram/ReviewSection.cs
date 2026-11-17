using System.Text;
using HhAutoApplier.Application.Commands.Vacancies;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.Vacancies;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// «Проверка», как reviewCard в вебе: одна карточка с листанием, одобрить, отклонить (с причиной), просьбы к письму (#72),
// правка письма и текстовых ответов анкеты. Правка — ответом на сообщение бота (решение владельца 2026-11-17, без миграции):
// бот присылает текущий текст в блоке <pre>, ответ даёт черновик — сообщение с письмом и ответами в тех же блоках.
// Черновик и есть состояние: «Одобрить с этим письмом» берёт текст из него, следующая правка — тоже из него
public class ReviewSection(
    GetVacanciesQueryHandler vacancies,
    GetVacancyQueryHandler vacancy,
    ReviewVacancyCommandHandler review,
    ResolveLetterRequestsCommandHandler resolve
) : IBotSection
{
    public const string Card = "rv";

    public const string EditLetter = "rl";

    public const string EditAnswer = "ra";

    public const string ApproveDraft = "rd";

    public const string CancelDraft = "rx";

    public const string DeclineWithReason = "rr";

    public const string ConfirmCodeWord = "rc";

    public const string FromProfile = "rp";

    // Пустой блок Telegram не покажет и порядок блоков собьётся — вместо пустоты пометка
    public const string NoLetter = "(письма нет)";

    public const string NoAnswer = "(нет ответа)";

    // Ограничение команды ReviewVacancyCommand и письма в домене
    private const int MaxLetter = 4000;

    private const int MaxAnswer = 2000;

    public IReadOnlyCollection<string> Actions =>
    [
        BotTexts.Approve, BotTexts.Decline, BotTexts.ShowReview, BotTexts.ReviewCard, Card, EditLetter, EditAnswer, ApproveDraft, CancelDraft,
        DeclineWithReason, ConfirmCodeWord, FromProfile,
    ];

    public async Task<BotCallbackResult> PressAsync(BotPress press, CancellationToken ct)
    {
        var id = press.Arg(0);
        switch (press.Action)
        {
            case BotTexts.Approve or BotTexts.Decline:
                return await DecideAsync(id, press.Action == BotTexts.Approve, null, null, ct);
            case BotTexts.ShowReview:
                // Кнопка сводки и прежнего «Статуса»: карточка новым сообщением, исходное не меняется
                return BotResults.Reply(await CardAsync(null, 0, ct));
            case BotTexts.ReviewCard:
                return BotResults.Edit(await CardAsync(id, 0, ct));
            case Card:
                return BotResults.Edit(await CardAsync(null, int.TryParse(id, out var skip) ? Math.Max(skip, 0) : 0, ct));
            case CancelDraft:
                return new BotCallbackResult("Отменено", "✖️ Черновик отменён");
            case ConfirmCodeWord or FromProfile:
                return await ResolveAsync(id, press.Action == ConfirmCodeWord && int.TryParse(press.Arg(1), out var n) ? [n] : [], ct);
        }

        if ((await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is not VacancyDto v)
            return BotResults.Notice("Вакансия не найдена");
        if (v.Status != VacancyStatus.Review)
            return BotResults.Notice($"Уже решено, статус: {Status(v.Status)}");

        switch (press.Action)
        {
            case EditLetter:
                return BotResults.Reply(Prompt(BotPrompt.Letter, v, Blocks(v, press.Source)));
            case EditAnswer when int.TryParse(press.Arg(1), out var number) && number >= 1 && number <= Editable(v).Count:
                return BotResults.Reply(Prompt(BotPrompt.Answer(number), v, Blocks(v, press.Source)));
            case DeclineWithReason:
                return BotResults.Reply(new BotMessage(
                    $"{BotPrompt.Header(BotPrompt.DeclineReason, v.Id)}\n{B(v.Title)}\nОтветьте на это сообщение причиной отказа — она попадёт в отчёт.")
                {
                    Html = true,
                    AskReply = "Причина отказа",
                });
            case ApproveDraft:
                return await ApproveDraftAsync(v, press.Source, ct);
            default:
                return BotResults.Notice("Неизвестная кнопка");
        }
    }

    // Отклики на проверке — как в вебе: по баллу, сначала лучшие
    private async Task<List<VacancyDto>> WaitingAsync(CancellationToken ct)
    {
        var list = (await vacancies.Handle(new GetVacanciesQuery { Status = VacancyStatus.Review, Take = 200 }, ct)).Value is List<VacancyDto> l ? l : [];
        return list.OrderByDescending(v => v.Score).ThenBy(v => v.Id, StringComparer.Ordinal).ToList();
    }

    // Карточка отклика: по id или по номеру в очереди проверки (после последнего — снова первый)
    public async Task<BotMessage> CardAsync(string? id, int skip, CancellationToken ct)
    {
        var waiting = await WaitingAsync(ct);
        var index = id is null ? (waiting.Count == 0 ? -1 : skip % waiting.Count) : waiting.FindIndex(v => v.Id == id);
        if (index < 0)
        {
            if (id is not null && (await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is VacancyDto decided)
                return new BotMessage($"Уже не на проверке.\n\n{VacanciesSection.CardText(decided)}")
                {
                    Html = true,
                    Rows = waiting.Count > 0 ? [[new BotButton($"📝 Проверка ({waiting.Count})", $"{Card}:0")]] : [],
                };

            return new BotMessage(id is null ? "Откликов на проверке нет" : "Вакансия не найдена") { Rows = [[new BotButton("📊 Обзор", $"{OverviewSection.Show}:")]] };
        }

        var v = waiting[index];
        var rows = new List<IReadOnlyList<BotButton>>
        {
            BotTexts.ReviewButtons(v.Id),
        };
        rows.AddRange(Rows(new[] { new BotButton("✏️ Письмо", $"{EditLetter}:{v.Id}") }
            .Concat(Editable(v).Select((_, k) => new BotButton($"✏️ Ответ {k + 1}", $"{EditAnswer}:{v.Id}:{k + 1}"))), 3));

        // Кодовое слово вставляется только по подтверждению, значения из профиля — по кнопке. Ссылки и контакты не выполняются
        foreach (var (r, i) in v.LetterRequests.Select((r, i) => (r, i)))
            if (!r.Closed && r.Type == LetterRequestType.CodeWord && r.Value is { } word)
                rows.Add([new BotButton($"🔑 Вставить слово «{Cut(word, 30)}»", $"{ConfirmCodeWord}:{v.Id}:{i}")]);
        if (v.LetterRequestsState == LetterRequestsState.Found
            && v.LetterRequests.Any(r => !r.Closed && r.Type is LetterRequestType.Salary or LetterRequestType.TestTask))
            rows.Add([new BotButton("👤 Подставить из профиля", $"{FromProfile}:{v.Id}")]);

        var links = new List<BotButton> { new("❌ С причиной", $"{DeclineWithReason}:{v.Id}"), new("📄 Описание", $"{VacanciesSection.Description}:{v.Id}") };
        if (SafeUrl(v.Url) is { } url)
            links.Add(BotButton.Link("🔗 hh.ru", url));
        rows.Add(links);
        if (waiting.Count > 1)
            rows.Add([new BotButton("Следующая →", $"{Card}:{index + 1}")]);

        return new BotMessage($"📝 {B("Проверка")} · {index + 1} из {waiting.Count}\n{CardText(v)}") { Html = true, Rows = rows };
    }

    private static string CardText(VacancyDto v)
    {
        var meta = new[]
        {
            v.Company, v.Area, Salary(v) is { Length: > 0 } s ? s : "зарплата не указана", v.WorkFormat, v.WorkExperience is { } exp ? $"опыт: {exp}" : "",
            $"балл {v.Score}", v.ResumeProfile is { } r ? $"резюме {r}" : "", v.HasTest ? "есть тест" : "",
        };
        var text = new StringBuilder()
            .Append($"<b>{Link(v.Title, v.Url)}</b>\n")
            .Append(E(string.Join(" · ", meta.Where(m => !string.IsNullOrWhiteSpace(m))))).Append('\n')
            .Append(I(Cut(v.StatusReason ?? v.LlmReason ?? "LLM сомневается", 500)));
        if (v.Stack.Length > 0)
            text.Append('\n').Append(E($"Стек: {string.Join(", ", v.Stack)}"));

        if (v.Questions.Count > 0)
        {
            text.Append($"\n\n{B($"Анкета ({v.Questions.Count})")}");
            var editable = Editable(v).Select(e => e.Index).ToList();
            foreach (var (q, i) in v.Questions.Select((q, i) => (q, i)))
            {
                var number = editable.IndexOf(i);
                var label = number >= 0 ? $" (ответ {number + 1})" : "";
                text.Append($"\n{i + 1}. {B(Cut(q.Question, 200))}{E(label)}\n{E(q.Answer.Count > 0 ? Cut(string.Join("; ", q.Answer), 500) : "ответа нет")}");
            }
        }

        text.Append(RequestsText(v));
        var model = v.CoverLetterModel is null ? "" : $" ({(v.CoverLetterModel == JobApplication.ManualLetter ? "ваше" : v.CoverLetterModel)})";
        text.Append(string.IsNullOrWhiteSpace(v.CoverLetter)
            ? v.LetterRequestsState is LetterRequestsState.Found or LetterRequestsState.Failed
                ? $"\n\n{B("Письма нет")}: без него одобрить нельзя — шаблон расширения не ответит на просьбы работодателя. «✏️ Письмо» — написать своё."
                : $"\n\n{B("Письма нет")}: расширение возьмёт шаблон. «✏️ Письмо» — написать своё."
            : $"\n\n{B($"Письмо{model}")}\n{Quote(Cut(v.CoverLetter, 2500))}");
        return text.ToString();
    }

    // Просьбы работодателя к письму (#72) — в карточке проверки и вакансии
    public static string RequestsText(VacancyDto v)
    {
        if (v.LetterRequestsState == LetterRequestsState.Failed)
            return $"\n\n{I("Просьбы работодателя к письму разобрать не удалось: прочитайте вакансию и напишите письмо сами.")}";
        if (v.LetterRequests.Count == 0)
            return "";

        var text = new StringBuilder($"\n\n{B($"Просьбы к письму ({v.LetterRequests.Count})")}");
        foreach (var r in v.LetterRequests)
        {
            var state = r.Closed ? r.Answer is { } a ? $"закрыта: {a}" : "решено человеком" : r.Problem ?? $"ответ: {r.Answer} — допишется к письму";
            text.Append($"\n— {B(RequestType(r.Type))}{(string.IsNullOrWhiteSpace(r.Text) ? "" : E($" — {Cut(r.Text, 300)}"))}\n  {E(state)}");
        }

        return text.ToString();
    }

    private static string RequestType(LetterRequestType type) => type switch
    {
        LetterRequestType.Arithmetic => "Пример",
        LetterRequestType.Salary => "Зарплата",
        LetterRequestType.TestTask => "Тестовое",
        LetterRequestType.CodeWord => "Кодовое слово",
        _ => "Прочее",
    };

    // Текстовые ответы анкеты, которые можно поправить, — как поля в вебе: только если LLM ответила хоть на один вопрос
    private static List<(VacancyQuestionDto Question, int Index)> Editable(VacancyDto v) =>
        v.Questions.Any(q => q.Answer.Count > 0)
            ? v.Questions.Select((q, i) => (q, i)).Where(x => x.q.Type == QuestionType.Text).ToList()
            : [];

    // Блоки черновика: письмо и правимые ответы по порядку. Из сообщения бота про ту же вакансию с тем же числом блоков — оттуда
    // (там уже сделанные правки), иначе — из базы
    private static List<string> Blocks(VacancyDto v, BotSource? source)
    {
        var editable = Editable(v);
        if (BotPrompt.Parse(source) is { } header && header.VacancyId == v.Id && source!.Blocks.Count == editable.Count + 1)
            return [.. source.Blocks];

        return
        [
            string.IsNullOrWhiteSpace(v.CoverLetter) ? NoLetter : v.CoverLetter,
            .. editable.Select(e => e.Question.Answer.Count > 0 ? string.Join("\n", e.Question.Answer) : NoAnswer),
        ];
    }

    private static string Body(VacancyDto v, IReadOnlyList<string> blocks)
    {
        var text = new StringBuilder($"{B(v.Title)}{(string.IsNullOrWhiteSpace(v.Company) ? "" : E($" — {v.Company}"))}\n\n{B("Письмо")}\n{Pre(blocks[0])}");
        foreach (var ((q, _), k) in Editable(v).Select((e, k) => (e, k)))
            text.Append($"\n{B($"Ответ {k + 1}.")} {E(Cut(q.Question, 200))}\n{Pre(blocks[k + 1])}");
        return text.ToString();
    }

    // Просьба ответить новым текстом: в сообщении — все блоки, чтобы сделанные правки не потерялись
    private static BotMessage Prompt(string kind, VacancyDto v, IReadOnlyList<string> blocks)
    {
        var what = BotPrompt.AnswerNumber(kind) is { } n ? $"ответом {n}" : "письмом";
        var text = $"{BotPrompt.Header(kind, v.Id)}\nОтветьте на это сообщение новым {what}. Текущий текст — в блоке ниже: нажмите на него, чтобы скопировать.\n\n{Body(v, blocks)}";
        return Fits(text) ?? new BotMessage(text) { Html = true, AskReply = BotPrompt.AnswerNumber(kind) is { } k ? $"Новый ответ {k}" : "Новое письмо" };
    }

    private static BotMessage Draft(VacancyDto v, IReadOnlyList<string> blocks)
    {
        var text = $"{BotPrompt.Header(BotPrompt.Draft, v.Id)}\nПроверьте и нажмите «Одобрить с этим письмом». Поправить ещё — кнопками ниже; ответ на это сообщение заменит письмо.\n\n{Body(v, blocks)}";
        var rows = new List<IReadOnlyList<BotButton>> { new[] { new BotButton("✅ Одобрить с этим письмом", $"{ApproveDraft}:{v.Id}") } };
        rows.AddRange(Rows(new[] { new BotButton("✏️ Письмо", $"{EditLetter}:{v.Id}") }
            .Concat(Editable(v).Select((_, k) => new BotButton($"✏️ Ответ {k + 1}", $"{EditAnswer}:{v.Id}:{k + 1}"))), 3));
        rows.Add([new BotButton("✖️ Отменить", $"{CancelDraft}:{v.Id}")]);
        return Fits(text) ?? new BotMessage(text) { Html = true, Rows = rows };
    }

    // Черновик не обрезать: обрезанное письмо ушло бы работодателю
    private static BotMessage? Fits(string html) =>
        VisibleLength(html) <= MaxText ? null
            : new BotMessage("Письмо с ответами анкеты не помещается в сообщение Telegram (4096 знаков): сократите текст или поправьте в веб-интерфейсе, раздел «Проверка».");

    // Ответ на просьбу бота: правка письма или ответа анкеты — новый черновик; причина отказа — отклонить
    public async Task<BotMessage> ReplyAsync(string kind, string vacancyId, string text, BotSource source, CancellationToken ct)
    {
        if ((await vacancy.Handle(new GetVacancyQuery(vacancyId), ct)).Value is not VacancyDto v)
            return new BotMessage("Вакансия не найдена");
        if (v.Status != VacancyStatus.Review)
            return new BotMessage($"Уже решено, статус: {Status(v.Status)}");

        var value = text.Trim();
        if (kind == BotPrompt.DeclineReason)
            return await DeclineAsync(v, value, ct);

        var blocks = Blocks(v, source);
        if (kind is BotPrompt.Letter or BotPrompt.Draft)
        {
            if (value.Length > MaxLetter)
                return new BotMessage($"Письмо длиннее {MaxLetter} знаков ({value.Length}) — сократите и ответьте снова.");
            blocks[0] = value;
        }
        else if (BotPrompt.AnswerNumber(kind) is { } n && n >= 1 && n < blocks.Count)
        {
            if (value.Length > MaxAnswer)
                return new BotMessage($"Ответ длиннее {MaxAnswer} знаков — сократите и ответьте снова.");
            blocks[n] = value;
        }
        else
            return new BotMessage("Не понял, что поправить: откройте карточку заново — /review");

        return Draft(v, blocks);
    }

    private async Task<BotCallbackResult> ApproveDraftAsync(VacancyDto v, BotSource? source, CancellationToken ct)
    {
        var editable = Editable(v);
        if (BotPrompt.Parse(source) is not { Kind: BotPrompt.Draft } header || header.VacancyId != v.Id || source!.Blocks.Count != editable.Count + 1)
            return BotResults.Notice("Черновик устарел — откройте карточку заново");

        var blocks = source.Blocks.Select(b => b.Trim()).ToList();
        // Как в вебе: письмо уходит, только если его поправили, иначе остаётся письмо LLM с её моделью;
        // ответы анкеты — все по порядку вопросов, если поправлен хоть один текстовый
        var letter = blocks[0] == NoLetter ? null : blocks[0];
        var coverLetter = letter is not null && letter != (v.CoverLetter ?? "").Trim() ? letter : null;
        var answerOf = editable.Select((e, k) => (e.Index, Text: blocks[k + 1])).ToDictionary(x => x.Index, x => x.Text);
        var edited = editable.Any(e => answerOf[e.Index] != string.Join("\n", e.Question.Answer).Trim());
        var answers = edited
            ? v.Questions.Select((q, i) => answerOf.TryGetValue(i, out var a) ? (a == NoAnswer ? [] : [a]) : q.Answer.ToList()).ToList()
            : null;
        return await DecideAsync(v.Id, true, coverLetter, answers, ct);
    }

    // Одобрить или отклонить — как «Проверка» в вебе (ReviewVacancyCommand). Решённое сообщение остаётся с итогом и кнопкой к следующему
    private async Task<BotCallbackResult> DecideAsync(string id, bool approve, string? letter, List<List<string>>? answers, CancellationToken ct)
    {
        var command = new ReviewVacancyCommand { VacancyId = id, Approve = approve, CoverLetter = letter, Answers = answers };
        var result = (await review.Handle(command, ct)).Value;
        switch (result)
        {
            case VacancyDto:
                var done = approve ? letter is null && answers is null ? "✅ Одобрено — в очереди" : "✅ Одобрено с вашими правками — в очереди" : "❌ Отклонено";
                return new BotCallbackResult(approve ? "Одобрено" : "Отклонено", done) { StatusRows = await NextRowsAsync(ct) };
            case NotFoundError:
                return new BotCallbackResult("Вакансия не найдена", "Вакансия не найдена");
            default:
                // Уже решено на странице проверки или другим нажатием — показать, что сейчас; иначе — почему нельзя (нет письма при просьбах)
                if ((await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is VacancyDto now && now.Status != VacancyStatus.Review)
                    return new BotCallbackResult("Уже решено", $"Уже решено, статус: {Status(now.Status)}");
                return BotResults.Notice(Cut(result is Error e ? e.Message : "Не удалось", 190));
        }
    }

    private async Task<BotMessage> DeclineAsync(VacancyDto v, string reason, CancellationToken ct)
    {
        var result = (await review.Handle(new ReviewVacancyCommand { VacancyId = v.Id, Approve = false, Reason = Cut(reason, 490) }, ct)).Value;
        if (result is not VacancyDto)
            return new BotMessage(result is Error e ? e.Message : "Не удалось");

        return new BotMessage($"❌ {B(v.Title)}: отклонено — {E(Cut(reason, 490))}") { Html = true, Rows = await NextRowsAsync(ct) };
    }

    private async Task<IReadOnlyList<IReadOnlyList<BotButton>>> NextRowsAsync(CancellationToken ct)
    {
        var left = (await WaitingAsync(ct)).Count;
        return left > 0 ? [[new BotButton($"Дальше → (на проверке {left})", $"{Card}:0")]] : [];
    }

    // Вставить кодовое слово или подставить значения из профиля — как кнопки просьб в вебе; карточка обновляется на месте
    private async Task<BotCallbackResult> ResolveAsync(string id, List<int> confirm, CancellationToken ct)
    {
        var result = (await resolve.Handle(new ResolveLetterRequestsCommand { VacancyId = id, ConfirmCodeWords = confirm }, ct)).Value;
        return result is VacancyDto
            ? BotResults.Edit(await CardAsync(id, 0, ct), "Письмо обновлено")
            : BotResults.Notice(Cut(result is Error e ? e.Message : "Не удалось", 190));
    }
}
