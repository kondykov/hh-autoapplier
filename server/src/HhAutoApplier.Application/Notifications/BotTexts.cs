using System.Text;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Dialogs;
using HhAutoApplier.Domain.Notifications;

namespace HhAutoApplier.Application.Notifications;

// Тексты бота. Простой текст без разметки: названия вакансий и письма не нужно экранировать
public static class BotTexts
{
    public const string Help = """
        Кнопки меню внизу чата или команды:
        /overview — обзор: счётчики по статусам, что дальше, отчёт CSV
        /review — проверка по одной карточке: одобрить, отклонить, поправить письмо и ответы анкеты
        /dialogs — ответы работодателей с черновиком ответа
        /vacancies — вакансии по статусу и поиску: в очередь, пропустить, оценить заново
        /searches — поиски: включить и выключить
        /collect — сбор вакансий (администратор)
        /help — эта справка
        /stop — отвязать чат

        Правка письма: «✏️ Письмо» под карточкой — бот пришлёт текущий текст, ответьте на его сообщение новым.
        Резюме, профиль, правила поисков, пароль и пользователи — в веб-интерфейсе.

        Уведомления: отклик на проверке, приглашения и вопросы работодателей с черновиком ответа, капча и дневной лимит в расширении, анкета без ответа LLM; администратору — сбои сбора и LLM.
        """;

    public const string NotLinked = "Чат не привязан. Получите код на backend (POST /api/telegram/link после входа по паролю) и отправьте сюда: /start <код>";

    // Кнопки меню присылают свой текст как обычное сообщение — бот понимает его как команду
    public const string OverviewButton = "📊 Обзор";

    public const string ReviewButton = "📝 Проверка";

    public const string DialogsButton = "💬 Ответы";

    public const string VacanciesButton = "📋 Вакансии";

    public const string SearchesButton = "🔎 Поиски";

    public const string HelpButton = "❓ Помощь";

    // Подписи меню до #115: у кого клавиатура не обновилась, кнопки продолжают работать
    public const string OldStatusButton = "📊 Статус";

    public const string OldReviewButton = "📝 На проверке";

    public static readonly IReadOnlyList<IReadOnlyList<string>> MenuRows =
        [[OverviewButton, ReviewButton, DialogsButton], [VacanciesButton, SearchesButton, HelpButton]];

    // Меню команд бота («/» в поле ввода)
    public static readonly IReadOnlyList<(string Command, string Description)> Commands =
    [
        ("overview", "Обзор: счётчики, что дальше, отчёт"),
        ("review", "Проверка откликов"),
        ("dialogs", "Ответы работодателей"),
        ("vacancies", "Вакансии по статусу и поиску"),
        ("searches", "Поиски: включить и выключить"),
        ("collect", "Сбор вакансий (администратор)"),
        ("help", "Справка"),
        ("stop", "Отвязать чат"),
    ];

    // Data кнопок: «действие:аргумент»; эти — с первой версии бота и остаются в уже отправленных сообщениях
    public const string Approve = "approve";

    public const string Decline = "decline";

    public const string ShowReview = "review";

    public const string Unlink = "unlink";

    public const string Cancel = "cancel";

    public const string Handled = "handled";

    // Карточка отклика на проверке (#115) — из уведомления и списка вакансий
    public const string ReviewCard = "rk";

    public static BotMessage UnlinkConfirm() =>
        new("Отвязать чат? Уведомления перестанут приходить, привязать снова можно только новым кодом.",
            [new BotButton("Отвязать", $"{Unlink}:"), new BotButton("Отмена", $"{Cancel}:")]);

    // Кнопки уведомления по его виду: отклик — решить или открыть карточку (правка письма), сводка — по одному,
    // ответ работодателя — открыть чат и отметить обработанным
    public static IReadOnlyList<IReadOnlyList<BotButton>> NotificationButtons(NotificationKind kind, string key, string? vacancyId) => kind switch
    {
        NotificationKind.Review when vacancyId is not null => [ReviewButtons(vacancyId), [new BotButton("📝 Карточка: письмо, анкета", $"{ReviewCard}:{vacancyId}")]],
        NotificationKind.Review when key.StartsWith("review-digest:") => [[new BotButton("📝 Решить по одному", $"{ShowReview}:")]],
        NotificationKind.EmployerReply when key.Split(':') is ["dialog", var chatId, ..] =>
            [[BotButton.Link("💬 Открыть чат", $"https://hh.ru/chat/{chatId}"), new BotButton("✔ Обработано", $"{Handled}:{chatId}")]],
        _ => [],
    };

    // Отклик на проверке (сомнительный или с ответами анкеты) для решения в чате; Data кнопок — «действие:id вакансии» (лимит Telegram — 64 байта)
    public static BotMessage Review(JobApplication application)
    {
        var v = application.Vacancy;
        var answered = application.Questions.Where(q => q.Answer.Count > 0).ToList();
        // У анкеты и письма на проверке причина — в статусе («анкета: проверить …»), пояснение оценки LLM — о другом
        var reason = application.Questions.Count > 0 || application.LetterRejected ? application.StatusReason : application.LlmReason ?? application.StatusReason;
        var text = new StringBuilder()
            .AppendLine($"На проверке: {v.Title}{(string.IsNullOrWhiteSpace(v.Company) ? "" : $" — {v.Company}")}")
            .AppendLine(v.Url)
            .AppendLine($"LLM {application.LlmScore ?? application.Score}: {reason ?? "без пояснения"}");
        if (!string.IsNullOrWhiteSpace(application.ResumeProfile))
            text.AppendLine($"Резюме: {application.ResumeProfile}");

        if (answered.Count > 0)
        {
            text.AppendLine().AppendLine("Анкета (поправить ответы — «📝 Карточка»):");
            foreach (var q in answered)
                text.AppendLine($"— {q.Text}").AppendLine($"  {string.Join("; ", q.Answer)}");
        }
        else if (application.Questions.Count > 0)
        {
            text.AppendLine().AppendLine("Ответов на анкету нет: «Одобрить» вернёт отклик в очередь, расширение пришлёт анкету заново. Ответить можно и вручную на hh.ru.");
        }

        if (application.LetterRequestsState == LetterRequestsState.Failed)
            text.AppendLine().AppendLine("Просьбы работодателя к письму разобрать не удалось: прочитайте вакансию и напишите письмо сами — «📝 Карточка».");
        else if (application.LetterRequests.Count > 0)
        {
            text.AppendLine().AppendLine("Просьбы работодателя к письму:");
            foreach (var r in application.LetterRequests)
                text.AppendLine($"— {r.Text}").AppendLine(r.Closed ? $"  ответ: {r.Answer ?? "решено человеком"}" : $"  не закрыта: {r.Problem ?? $"ждёт письма, значение {r.Answer}"}");
            if (application.AwaitsClosingLetter)
                text.AppendLine("Незакрытые просьбы — в «📝 Карточке»: вставить кодовое слово, подставить из профиля, поправить письмо.");
        }

        text.AppendLine().AppendLine(string.IsNullOrWhiteSpace(application.CoverLetter)
            ? "Письма пока нет — расширение возьмёт свой шаблон. Написать своё — «📝 Карточка» → «✏️ Письмо»."
            : $"Письмо (поправить — «📝 Карточка» → «✏️ Письмо»):\n{application.CoverLetter}");

        return new BotMessage(text.ToString().TrimEnd(), ReviewButtons(application.VacancyId));
    }

    // Ответ работодателя с разбором LLM и черновиком; отвечает человек — в чате hh.ru
    public static string EmployerReply(EmployerMessage message)
    {
        var kind = message.Kind switch
        {
            DialogKind.Invitation => "Приглашение",
            DialogKind.Question => "Вопрос работодателя",
            DialogKind.Rejection => "Отказ",
            _ => "Сообщение работодателя",
        };
        var text = new StringBuilder()
            .AppendLine($"{kind}: {message.Title}{(string.IsNullOrWhiteSpace(message.Company) ? "" : $" — {message.Company}")}")
            .AppendLine($"https://hh.ru/chat/{message.ChatId}");
        if (!string.IsNullOrWhiteSpace(message.Summary))
            text.AppendLine($"Суть: {message.Summary}");

        text.AppendLine().AppendLine("Сообщение:").AppendLine(message.Text);
        if (!string.IsNullOrWhiteSpace(message.SuggestedReply))
            text.AppendLine().AppendLine("Черновик ответа (отправьте сами в чате, поправив [заглушки]):").AppendLine(message.SuggestedReply);

        return text.AppendLine().Append("Все ответы — /dialogs или «💬 Ответы» в меню").ToString();
    }

    // Сводка вместо пачки сообщений: решать по одной карточке — /review или в веб-интерфейсе
    public static string ReviewDigest(IReadOnlyCollection<JobApplication> fresh, int total)
    {
        const int shown = 10;
        var text = new StringBuilder().AppendLine($"На проверке новых: {fresh.Count} (всего {total})");
        foreach (var a in fresh.OrderByDescending(a => a.LlmScore ?? a.Score).Take(shown))
            text.AppendLine($"• {a.Vacancy.Title}{(string.IsNullOrWhiteSpace(a.Vacancy.Company) ? "" : $" — {a.Vacancy.Company}")} (LLM {a.LlmScore ?? a.Score})");
        if (fresh.Count > shown)
            text.AppendLine($"…и ещё {fresh.Count - shown}");

        return text.AppendLine().Append("Решить: /review — по одной карточке, или в веб-интерфейсе, раздел «Проверка»").ToString();
    }

    public static IReadOnlyList<BotButton> ReviewButtons(string vacancyId) =>
    [
        new BotButton("✅ Одобрить", $"{Approve}:{vacancyId}"),
        new BotButton("❌ Отклонить", $"{Decline}:{vacancyId}"),
    ];
}
