namespace HhAutoApplier.Domain.Notifications;

public enum NotificationKind
{
    // Сомнительный отклик ждёт ручной проверки: в сообщении кнопки «Одобрить» и «Отклонить»
    Review = 0,
    // Расширение встретило капчу и остановилось
    Captcha = 1,
    // Расширение исчерпало дневной лимит откликов
    DailyLimit = 2,
    // LLM не ответила на анкету: отклик пропущен
    Questionnaire = 3,
    // Администратору: источник вакансий заблокировал сбор (антибот)
    SourceBlocked = 4,
    // Администратору: LLM недоступна или исчерпан дневной лимит запросов
    LlmUnavailable = 5,
    // Работодатель ответил в чате (приглашение, вопрос): разбор LLM и черновик ответа
    EmployerReply = 6,
    // Администратору: LLM часто даёт ответы, которые не проходят проверку
    LlmQuality = 7,
    // Расширение увидело, что вход в hh.ru слетел, и остановило очередь (#95)
    SignedOut = 8,
    // Сводка обычных уведомлений, которые не дошли, пока не было связи с Telegram (#132)
    MissedDigest = 9,
    // Просьба работодателя пройти анкету или тест вне hh.ru (#126).
    ExternalLink = 10,
}

// Агрегат: уведомление пользователю в Telegram (outbox). Сценарий записывает его вместе со своими данными,
// фоновая отправка доставляет. Key — защита от повторов: одно событие (отклик на проверке, капча за час) — одно сообщение
public class Notification
{
    // Для EF
    private Notification()
    {
    }

    public long Id { get; private set; }

    public Guid UserId { get; private set; }

    public NotificationKind Kind { get; private set; }

    public string Key { get; private set; } = "";

    public string Text { get; private set; } = "";

    // Отклик на проверке: кнопки бота действуют на эту вакансию
    public string? VacancyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public int Attempts { get; private set; }

    public string? Error { get; private set; }

    // Попытки кончились из-за связи (#132): уведомление ждёт, пока проверка снова увидит Telegram
    public DateTimeOffset? WaitingSince { get; private set; }

    public bool Pending => SentAt is null && Attempts < MaxAttempts;

    public bool Waiting => SentAt is null && WaitingSince is not null;

    // Ждущее старше суток не досылается: новость уже не нужна (решение владельца 2026-11-18)
    public bool Stale(DateTimeOffset now) => now - CreatedAt > MaxWait;

    public static Notification Create(Guid userId, NotificationKind kind, string key, string text, DateTimeOffset at, string? vacancyId = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DomainException("У уведомления нет ключа");

        if (string.IsNullOrWhiteSpace(text))
            throw new DomainException("Пустое уведомление");

        text = text.Trim();
        return new Notification
        {
            UserId = userId,
            Kind = kind,
            Key = key,
            // Лимит сообщения Telegram — 4096 символов
            Text = text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text,
            VacancyId = vacancyId,
            CreatedAt = at,
        };
    }

    public void MarkSent(DateTimeOffset at) => (SentAt, Error, WaitingSince) = (at, null, null);

    // Неудачная попытка, Telegram ответил отказом; после MaxAttempts уведомление больше не отправляется
    public void MarkFailed(string error)
    {
        Attempts++;
        Error = Cut(error);
    }

    // Неудачная попытка из-за связи (сеть, прокси, таймаут): после MaxAttempts уведомление не теряется, а ждёт связи
    public void MarkLinkFailed(string error, DateTimeOffset at)
    {
        MarkFailed(error);
        if (Attempts >= MaxAttempts && SentAt is null)
            WaitingSince ??= at;
    }

    // Связь вернулась: новые попытки, как у только что записанного. Старше суток — «устарело», не слать; false — не возобновлено
    public bool Resume(DateTimeOffset now)
    {
        if (!Waiting)
            throw new DomainException("Уведомление не ждёт связи");

        WaitingSince = null;
        if (Stale(now))
        {
            Error = Expired;
            return false;
        }

        (Attempts, Error) = (0, null);
        return true;
    }

    // Связь вернулась, обычное уведомление вошло в сводку пропущенного (MissedDigest): отдельно не слать
    public void CoverByDigest()
    {
        if (!Waiting)
            throw new DomainException("Уведомление не ждёт связи");

        WaitingSince = null;
        Cancel(InMissedDigest);
    }

    // Доставить некуда (чат отвязан, бот заблокирован пользователем): больше не пытаться
    public void Cancel(string reason)
    {
        Attempts = MaxAttempts;
        WaitingSince = null;
        Error = Cut(reason);
    }

    private static string Cut(string text) => text.Length > 500 ? text[..500] : text;

    public const string Expired = "устарело";

    public const string InMissedDigest = "в сводке пропущенного";

    public static readonly TimeSpan MaxWait = TimeSpan.FromDays(1);

    public const int MaxAttempts = 5;

    public const int MaxTextLength = 4000;
}
