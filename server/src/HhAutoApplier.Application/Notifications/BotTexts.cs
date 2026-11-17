using System.Text;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Dialogs;
using HhAutoApplier.Domain.Notifications;

namespace HhAutoApplier.Application.Notifications;

// Как бот доставляет уведомление (#115, решение владельца 2026-11-17): важное — отдельным коротким сообщением с кнопками действий,
// обычное — строкой в сводку, которая дописывается правкой без нового звука
public enum NotificationDelivery
{
    Important,
    Digest,
}

// Тексты уведомлений и команды бота. Уведомления — простой текст без разметки: названия вакансий и письма не нужно экранировать.
// Экраны разделов — в Application/Telegram
public static class BotTexts
{
    public const string Help = """
        Бот — одно сообщение-экран: кнопки перерисовывают его на месте, новых сообщений нет. Открыть экран заново внизу чата — /menu.

        Разделы: 📊 Обзор (счётчики, что дальше, отчёт CSV), 📝 Проверка (одобрить, отклонить, поправить письмо и ответы анкеты), 💬 Ответы работодателей (черновик копируется нажатием), 📋 Вакансии (в очередь, пропустить, оценить заново), 🔎 Поиски (включить и выключить), 🛠 Сбор (администратор).

        Текст — письмо, ответ анкеты, причину — пишите обычным сообщением, когда экран его ждёт: бот заберёт его и уберёт из чата.

        Уведомления: капча, нет входа, дневной лимит, ответ работодателя, сбои — отдельными сообщениями; новые отклики на проверке и анкеты — одной сводкой, которая дописывается.

        Резюме, профиль, правила поисков, пароль и пользователи — в веб-интерфейсе. /stop — отвязать чат.
        """;

    public const string NotLinked = "Чат не привязан. Получите код на backend (POST /api/telegram/link после входа по паролю) и отправьте сюда: /start <код>";

    // Подписи клавиатуры-меню прежних версий: у кого она осталась, кнопки продолжают работать, пока /menu её не уберёт
    public const string OldStatusButton = "📊 Статус";

    public const string OldReviewButton = "📝 На проверке";

    public const string OldHelpButton = "❓ Помощь";

    // Меню команд бота («/» в поле ввода)
    public static readonly IReadOnlyList<(string Command, string Description)> Commands =
    [
        ("menu", "Экран бота"),
        ("review", "Проверка откликов"),
        ("dialogs", "Ответы работодателей"),
        ("vacancies", "Вакансии"),
        ("overview", "Обзор и отчёт"),
        ("help", "Справка"),
        ("stop", "Отвязать чат"),
    ];

    // Data кнопок уведомлений: «действие:аргумент»; эти — с первой версии бота и остаются в уже отправленных сообщениях
    public const string Approve = "approve";

    public const string Decline = "decline";

    public const string ShowReview = "review";

    public const string Unlink = "unlink";

    public const string Cancel = "cancel";

    public const string Handled = "handled";

    // Карточка отклика на проверке (#115, кнопка уведомлений до сводок)
    public const string ReviewCard = "rk";

    // «Открыть»: остальная data — экран, который прислать заново внизу чата (уведомление не меняется)
    public const string Open = "o";

    // «Понятно»: убрать уведомление
    public const string Dismiss = "ok";

    // Вид уведомления и кнопки — в одном месте. Обычные: новые отклики на проверке и анкеты без ответа LLM; остальное — важное
    public static NotificationDelivery Delivery(NotificationKind kind) => kind switch
    {
        NotificationKind.Review or NotificationKind.Questionnaire => NotificationDelivery.Digest,
        _ => NotificationDelivery.Important,
    };

    // Кнопки важного уведомления по его виду; у сводки кнопки — по счётчикам (BotDigest)
    public static IReadOnlyList<IReadOnlyList<BotButton>> NotificationButtons(NotificationKind kind, string key, string? vacancyId) => kind switch
    {
        NotificationKind.EmployerReply when key.Split(':') is ["dialog", var chatId, ..] =>
        [
            [BotButton.Link("💬 Открыть чат", $"https://hh.ru/chat/{chatId}"), new BotButton("✔ Обработано", $"{Handled}:{chatId}")],
            [new BotButton("📝 Подробнее", $"{Open}:dc:{chatId}:0:o")],
        ],
        NotificationKind.SourceBlocked => [[new BotButton("🛠 Сбор", $"{Open}:cs:"), new BotButton("✔ Понятно", $"{Dismiss}:")]],
        NotificationKind.Captcha or NotificationKind.DailyLimit or NotificationKind.SignedOut or NotificationKind.LlmUnavailable or NotificationKind.LlmQuality =>
            [[new BotButton("✔ Понятно", $"{Dismiss}:")]],
        _ => [],
    };

    // Строка сводки: отклик ждёт проверки
    public static string ReviewLine(JobApplication application)
    {
        var v = application.Vacancy;
        // У анкеты и письма на проверке причина — в статусе («анкета: проверить …»), пояснение оценки LLM — о другом
        var reason = application.Questions.Count > 0 || application.LetterRejected || application.AwaitsClosingLetter
            ? application.StatusReason
            : application.LlmReason ?? application.StatusReason;
        var line = $"📝 {v.Title}{(string.IsNullOrWhiteSpace(v.Company) ? "" : $" — {v.Company}")} (LLM {application.LlmScore ?? application.Score})";
        return string.IsNullOrWhiteSpace(reason) ? line : $"{line}: {Short(reason, 150)}";
    }

    public static string Short(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    // Ответ работодателя с разбором LLM и черновиком; отвечает человек — в чате hh.ru. Коротко: полностью — «Подробнее»
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
            .AppendLine($"💬 {kind}: {message.Title}{(string.IsNullOrWhiteSpace(message.Company) ? "" : $" — {message.Company}")}")
            .AppendLine($"https://hh.ru/chat/{message.ChatId}");
        text.AppendLine(string.IsNullOrWhiteSpace(message.Summary) ? Short(message.Text, 300) : $"Суть: {message.Summary}");
        if (!string.IsNullOrWhiteSpace(message.SuggestedReply))
            text.AppendLine().AppendLine("Черновик ответа (отправьте сами в чате, поправив [заглушки]):").AppendLine(Short(message.SuggestedReply, 1500));

        return text.ToString().TrimEnd();
    }

    // Много новых откликов за проход — одна строка-итог вместо десятка
    public static string ReviewDigest(IReadOnlyCollection<JobApplication> fresh, int total)
    {
        const int shown = 5;
        var text = new StringBuilder().AppendLine($"📝 На проверке новых: {fresh.Count} (всего {total})");
        foreach (var a in fresh.OrderByDescending(a => a.LlmScore ?? a.Score).Take(shown))
            text.AppendLine($"• {a.Vacancy.Title}{(string.IsNullOrWhiteSpace(a.Vacancy.Company) ? "" : $" — {a.Vacancy.Company}")} (LLM {a.LlmScore ?? a.Score})");
        if (fresh.Count > shown)
            text.AppendLine($"…и ещё {fresh.Count - shown}");

        return text.Append("Решить: /review").ToString();
    }
}
