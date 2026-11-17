using System.Text;
using HhAutoApplier.Application.Commands.Vacancies;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Queries.Vacancies;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// «Проверка», как reviewCard в вебе, на одном экране (#115): карточка с листанием, одобрить и отклонить — экран сам переходит
// к следующей; отклонить с причиной; просьбы к письму (#72); правка письма и текстовых ответов анкеты. Правка — режимом ввода:
// экран показывает текущий текст и ждёт новый обычным сообщением; правки копятся в черновике TelegramChat до «Одобрить с этим письмом»
public class ReviewSection(
    GetVacanciesQueryHandler vacancies,
    GetVacancyQueryHandler vacancy,
    ReviewVacancyCommandHandler review,
    ResolveLetterRequestsCommandHandler resolve,
    BotChat chat
) : IBotSection
{
    public const string Card = "rv";

    public const string ApproveOnScreen = "ry";

    public const string DeclineOnScreen = "rn";

    public const string EditLetter = "rl";

    public const string EditAnswer = "ra";

    public const string ApproveDraft = "rd";

    public const string CancelDraft = "rx";

    public const string DeclineWithReason = "rr";

    public const string ConfirmCodeWord = "rc";

    public const string FromProfile = "rp";

    // Пустой текст Telegram не покажет — вместо пустоты пометка
    public const string NoLetter = "(письма нет)";

    public const string NoAnswer = "(нет ответа)";

    // Ограничение письма в домене и ReviewVacancyCommand
    public const int MaxLetter = 4000;

    private const int MaxAnswer = 2000;

    public IReadOnlyCollection<string> Actions =>
    [
        BotTexts.Approve, BotTexts.Decline, BotTexts.ShowReview, BotTexts.ReviewCard, Card, ApproveOnScreen, DeclineOnScreen, EditLetter, EditAnswer,
        ApproveDraft, CancelDraft, DeclineWithReason, ConfirmCodeWord, FromProfile,
    ];

    public async Task<BotResult> PressAsync(BotPress press, CancellationToken ct)
    {
        var id = press.Arg(0);
        switch (press.Action)
        {
            case BotTexts.Approve or BotTexts.Decline:
                return await DecideFromNotificationAsync(id, press.Action == BotTexts.Approve, ct);
            case BotTexts.ShowReview:
                return BotResults.Show(await CardAsync(null, 0, ct));
            case BotTexts.ReviewCard:
                return BotResults.Show(await CardAsync(id, 0, ct));
            case Card:
                return BotResults.Show(await CardAsync(null, int.TryParse(id, out var skip) ? Math.Max(skip, 0) : 0, ct));
            case CancelDraft:
                await chat.ClearDraftAsync(ct);
                return BotResults.Show(await CardAsync(id, 0, ct), "Правка отменена");
            case ConfirmCodeWord or FromProfile:
                return await ResolveAsync(id, press.Action == ConfirmCodeWord && int.TryParse(press.Arg(1), out var n) ? [n] : [], ct);
        }

        if ((await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is not VacancyDto v)
            return BotResults.Notice("Вакансия не найдена");
        if (v.Status != VacancyStatus.Review)
            return BotResults.Show(await CardAsync(id, 0, ct), $"Уже решено: {Status(v.Status)}");

        switch (press.Action)
        {
            case ApproveOnScreen or DeclineOnScreen:
                return await DecideAsync(v, press.Action == ApproveOnScreen, null, null, null, ct);
            case ApproveDraft:
                return await ApproveDraftAsync(v, ct);
            case EditLetter:
                return await EditAsync(v, 0, ct);
            case EditAnswer when int.TryParse(press.Arg(1), out var number) && number >= 1 && number <= Editable(v).Count:
                return await EditAsync(v, number, ct);
            case DeclineWithReason:
                await chat.AwaitInputAsync(BotInput.DeclineReason, v.Id, null, $"{BotTexts.ReviewCard}:{v.Id}", ct);
                return BotResults.Show(BotInput.Screen($"❌ {B("Причина отказа")}\n{B(v.Title)}\n\nНапишите причину обычным сообщением — она попадёт в отчёт."));
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
        var menu = new BotButton("☰ Меню", $"{MenuSection.Show}:");
        if (index < 0)
        {
            if (id is not null && (await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is VacancyDto decided)
                return new BotMessage($"Уже не на проверке.\n\n{VacanciesSection.CardText(decided)}")
                {
                    Html = true,
                    Rows = waiting.Count > 0 ? [[new BotButton($"📝 Проверка ({waiting.Count})", $"{Card}:0"), menu]] : [[menu]],
                };

            return new BotMessage(id is null ? "Откликов на проверке нет ✨" : "Вакансия не найдена") { Rows = [[menu]] };
        }

        var v = waiting[index];
        var draft = chat.Draft(v.Id, Editable(v).Count + 1);
        var rows = new List<IReadOnlyList<BotButton>>
        {
            draft is null
                ? [new BotButton("✅ Одобрить", $"{ApproveOnScreen}:{v.Id}"), new BotButton("❌ Отклонить", $"{DeclineOnScreen}:{v.Id}")]
                : [new BotButton("✅ Одобрить с этим письмом", $"{ApproveDraft}:{v.Id}"), new BotButton("✖️ Отменить правку", $"{CancelDraft}:{v.Id}")],
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

        var links = new List<BotButton> { new("❌ С причиной", $"{DeclineWithReason}:{v.Id}"), new("📄 Описание", $"{VacanciesSection.Description}:{v.Id}:r") };
        if (SafeUrl(v.Url) is { } url)
            links.Add(BotButton.Link("🔗 hh.ru", url));
        rows.Add(links);
        rows.Add(waiting.Count > 1 ? [new BotButton("Следующая →", $"{Card}:{index + 1}"), menu] : [menu]);

        var header = $"📝 {B("Проверка")} · {index + 1} из {waiting.Count}{(draft is null ? "" : " · ✏️ есть правка")}";
        return new BotMessage($"{header}\n{CardText(v, draft)}") { Html = true, Rows = rows };
    }

    private static string CardText(VacancyDto v, IReadOnlyList<string>? draft)
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

        var editable = Editable(v).Select(e => e.Index).ToList();
        if (v.Questions.Count > 0)
        {
            text.Append($"\n\n{B($"Анкета ({v.Questions.Count})")}");
            foreach (var (q, i) in v.Questions.Select((q, i) => (q, i)))
            {
                var number = editable.IndexOf(i);
                var label = number >= 0 ? $" (ответ {number + 1}{(draft is not null && draft[number + 1] != AnswerText(q) ? ", ✏️ поправлен" : "")})" : "";
                var answer = number >= 0 && draft is not null ? draft[number + 1] : q.Answer.Count > 0 ? string.Join("; ", q.Answer) : "ответа нет";
                text.Append($"\n{i + 1}. {B(Cut(q.Question, 200))}{E(label)}\n{E(Cut(answer, 500))}");
            }
        }

        text.Append(RequestsText(v));
        var letter = draft is not null ? draft[0] == NoLetter ? null : draft[0] : v.CoverLetter;
        var model = draft is not null && letter != v.CoverLetter ? " (✏️ ваша правка)"
            : v.CoverLetterModel is null ? "" : $" ({(v.CoverLetterModel == JobApplication.ManualLetter ? "ваше" : v.CoverLetterModel)})";
        text.Append(string.IsNullOrWhiteSpace(letter)
            ? v.LetterRequestsState is LetterRequestsState.Found or LetterRequestsState.Failed
                ? $"\n\n{B("Письма нет")}: без него одобрить нельзя — шаблон расширения не ответит на просьбы работодателя. «✏️ Письмо» — написать своё."
                : $"\n\n{B("Письма нет")}: расширение возьмёт шаблон. «✏️ Письмо» — написать своё."
            : $"\n\n{B($"Письмо{model}")}\n{Quote(Cut(letter, 2500))}");
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

    private static string AnswerText(VacancyQuestionDto q) => q.Answer.Count > 0 ? string.Join("\n", q.Answer) : NoAnswer;

    // Блоки правки: письмо и правимые ответы по порядку — из черновика чата или из отклика
    private List<string> Blocks(VacancyDto v)
    {
        var editable = Editable(v);
        return chat.Draft(v.Id, editable.Count + 1) is { } draft
            ? [.. draft]
            : [string.IsNullOrWhiteSpace(v.CoverLetter) ? NoLetter : v.CoverLetter, .. editable.Select(e => AnswerText(e.Question))];
    }

    // Режим ввода письма (block = 0) или ответа анкеты: экран показывает текущий текст — нажатие копирует его
    private async Task<BotResult> EditAsync(VacancyDto v, int block, CancellationToken ct)
    {
        var current = Blocks(v)[block];
        await chat.AwaitInputAsync(block == 0 ? BotInput.Letter : BotInput.Answer, v.Id, block == 0 ? null : block, $"{BotTexts.ReviewCard}:{v.Id}", ct);
        var what = block == 0 ? "Письмо" : $"Ответ {block}. {Cut(Editable(v)[block - 1].Question.Question, 200)}";
        var text = $"✏️ {B(what)}\n{B(v.Title)}\n\nТекущий текст — нажмите, чтобы скопировать:\n{Pre(Cut(current, 3000))}\n\nНапишите новый текст обычным сообщением: бот заберёт его в правку и уберёт из чата.";
        return BotResults.Show(BotInput.Screen(text));
    }

    // Текст для режима ввода экрана: правка — в черновик и снова карточка; причина отказа — отклонить и к следующей
    public async Task<BotResult> InputAsync(string kind, string vacancyId, int? number, string text, CancellationToken ct)
    {
        if ((await vacancy.Handle(new GetVacancyQuery(vacancyId), ct)).Value is not VacancyDto v)
            return BotResults.Show(await CardAsync(null, 0, ct), "Вакансия не найдена");
        if (v.Status != VacancyStatus.Review)
            return BotResults.Show(await CardAsync(vacancyId, 0, ct), $"Уже решено: {Status(v.Status)}");

        var value = text.Trim();
        if (kind == BotInput.DeclineReason)
            return await DecideAsync(v, false, null, null, Cut(value, 490), ct);

        var blocks = Blocks(v);
        var block = kind == BotInput.Letter ? 0 : number ?? -1;
        var max = block == 0 ? MaxLetter : MaxAnswer;
        if (block < 0 || block >= blocks.Count)
            return BotResults.Show(await CardAsync(v.Id, 0, ct), "Не понял, что поправить");
        if (value.Length > max)
            return BotResults.Show(await CardAsync(v.Id, 0, ct), $"Длиннее {max} знаков ({value.Length}) — сократите и попробуйте снова");

        blocks[block] = value;
        await chat.SaveDraftAsync(v.Id, blocks, ct);
        return BotResults.Show(await CardAsync(v.Id, 0, ct), "Правка в черновике — одобрите с этим письмом");
    }

    private async Task<BotResult> ApproveDraftAsync(VacancyDto v, CancellationToken ct)
    {
        var editable = Editable(v);
        if (chat.Draft(v.Id, editable.Count + 1) is not { } draft)
            return BotResults.Show(await CardAsync(v.Id, 0, ct), "Правки нет — карточка обновлена");

        var blocks = draft.Select(b => b.Trim()).ToList();
        // Как в вебе: письмо уходит, только если его поправили, иначе остаётся письмо LLM с её моделью;
        // ответы анкеты — все по порядку вопросов, если поправлен хоть один текстовый
        var letter = blocks[0] == NoLetter ? null : blocks[0];
        var coverLetter = letter is not null && letter != (v.CoverLetter ?? "").Trim() ? letter : null;
        var answerOf = editable.Select((e, k) => (e.Index, Text: blocks[k + 1])).ToDictionary(x => x.Index, x => x.Text);
        var edited = editable.Any(e => answerOf[e.Index] != AnswerText(e.Question).Trim());
        var answers = edited
            ? v.Questions.Select((q, i) => answerOf.TryGetValue(i, out var a) ? (a == NoAnswer ? [] : [a]) : q.Answer.ToList()).ToList()
            : null;
        return await DecideAsync(v, true, coverLetter, answers, null, ct);
    }

    // Решение на экране — как «Проверка» в вебе (ReviewVacancyCommand); экран переходит к следующей карточке, итог — всплывашкой
    private async Task<BotResult> DecideAsync(VacancyDto v, bool approve, string? letter, List<List<string>>? answers, string? reason, CancellationToken ct)
    {
        var command = new ReviewVacancyCommand { VacancyId = v.Id, Approve = approve, CoverLetter = letter, Answers = answers, Reason = reason };
        var result = (await review.Handle(command, ct)).Value;
        if (result is not VacancyDto)
            return BotResults.Show(await CardAsync(v.Id, 0, ct), Cut(result is Error e ? e.Message : "Не удалось", 190));

        await chat.ClearDraftAsync(ct);
        var left = (await WaitingAsync(ct)).Count;
        var done = approve ? letter is null && answers is null ? "✅ Одобрено" : "✅ Одобрено с вашими правками" : "❌ Отклонено";
        return BotResults.Show(await CardAsync(null, 0, ct), left > 0 ? $"{done} · осталось {left}" : $"{done} · проверка пуста");
    }

    // Кнопки под уведомлением прежних версий: уведомление сворачивается до итога
    private async Task<BotResult> DecideFromNotificationAsync(string id, bool approve, CancellationToken ct)
    {
        var result = (await review.Handle(new ReviewVacancyCommand { VacancyId = id, Approve = approve }, ct)).Value;
        switch (result)
        {
            case VacancyDto:
                return new BotResult { Notice = approve ? "Одобрено" : "Отклонено", Status = approve ? "✅ Одобрено — в очереди" : "❌ Отклонено" };
            case NotFoundError:
                return new BotResult { Notice = "Вакансия не найдена", Status = "Вакансия не найдена" };
            default:
                // Уже решено на странице проверки или другим нажатием — показать, что сейчас; иначе — почему нельзя (нет письма при просьбах)
                if ((await vacancy.Handle(new GetVacancyQuery(id), ct)).Value is VacancyDto now && now.Status != VacancyStatus.Review)
                    return new BotResult { Notice = "Уже решено", Status = $"Уже решено, статус: {Status(now.Status)}" };
                return BotResults.Notice(Cut(result is Error e ? e.Message : "Не удалось", 190));
        }
    }

    // Вставить кодовое слово или подставить значения из профиля — как кнопки просьб в вебе; карточка обновляется на месте
    private async Task<BotResult> ResolveAsync(string id, List<int> confirm, CancellationToken ct)
    {
        var result = (await resolve.Handle(new ResolveLetterRequestsCommand { VacancyId = id, ConfirmCodeWords = confirm }, ct)).Value;
        return result is VacancyDto
            ? BotResults.Show(await CardAsync(id, 0, ct), "Письмо обновлено")
            : BotResults.Notice(Cut(result is Error e ? e.Message : "Не удалось", 190));
    }
}
