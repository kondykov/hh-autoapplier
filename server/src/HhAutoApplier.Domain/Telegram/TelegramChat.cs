using System.Security.Cryptography;
using System.Text;

namespace HhAutoApplier.Domain.Telegram;

// Агрегат: чат Telegram пользователя — куда бот шлёт уведомления и откуда принимает команды. У пользователя один чат.
// Привязка — одноразовым кодом: пользователь получает его после входа по паролю и отправляет боту /start <код>.
// В базе только хэш кода; новый код не отвязывает прежний чат, пока не привяжется новый
public class TelegramChat
{
    // Для EF
    private TelegramChat()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    // Личный чат с ботом; null — ещё не привязан
    public long? ChatId { get; private set; }

    // Имя в Telegram (@username) — чтобы узнать чат в настройках
    public string? Username { get; private set; }

    public DateTimeOffset? LinkedAt { get; private set; }

    // SHA-256 кода привязки; null — кода нет или уже использован
    public string? CodeHash { get; private set; }

    public DateTimeOffset? CodeExpiresAt { get; private set; }

    public bool Linked => ChatId is not null;

    public static TelegramChat Create(Guid userId) => new() { Id = Guid.NewGuid(), UserId = userId };

    // Новый код привязки (открытое значение — только сейчас); прежний код перестаёт действовать
    public string IssueCode(DateTimeOffset at)
    {
        // Код идёт в ссылку t.me/<бот>?start=<код>: там допустимы только A-Z, a-z, 0-9, _ и -, не длиннее 64 символов
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        (CodeHash, CodeExpiresAt) = (HashOf(code), at + CodeLifetime);
        return code;
    }

    public bool CodeValid(DateTimeOffset at) => CodeHash is not null && CodeExpiresAt is { } expires && at <= expires;

    public void Link(long chatId, string? username, DateTimeOffset at)
    {
        if (!CodeValid(at))
            throw new DomainException("Код привязки устарел — получите новый");

        (ChatId, Username, LinkedAt) = (chatId, string.IsNullOrWhiteSpace(username) ? null : username.Trim(), at);
        (CodeHash, CodeExpiresAt) = (null, null);
        // Экран и сводка были в прежнем чате
        ScreenMessageId = null;
        ClearInput();
        ClearDraft();
        ResetDigest();
    }

    // Отвязать: бот перестаёт слать уведомления и принимать команды из чата; экран, ввод, черновик и сводка забываются
    public void Unlink()
    {
        (ChatId, Username, LinkedAt) = (null, null, null);
        ScreenMessageId = null;
        ClearInput();
        ClearDraft();
        ResetDigest();
    }

    // ── «Один экран» (#115): одно сообщение бота, которое перерисовывается на месте ───────────────────

    // Сообщение-экран в чате; null — экрана нет, следующий ответ придёт новым сообщением
    public int? ScreenMessageId { get; private set; }

    public void ShowScreen(int messageId) => ScreenMessageId = messageId > 0 ? messageId : throw new DomainException("Нет id сообщения-экрана");

    // Режим ввода: экран ждёт текст обычным сообщением — что ждём (Kind), для чего (Target: id вакансии), номер ответа анкеты,
    // куда вернуться (Back — callback_data экрана). Протухает через InputLifetime
    public string? InputKind { get; private set; }

    public string? InputTarget { get; private set; }

    public int? InputNumber { get; private set; }

    public string? InputBack { get; private set; }

    public DateTimeOffset? InputSince { get; private set; }

    public void AwaitInput(string kind, string target, int? number, string back, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > MaxKey || string.IsNullOrWhiteSpace(target) || target.Length > MaxKey || back.Length > MaxKey)
            throw new DomainException("Неверный режим ввода");

        (InputKind, InputTarget, InputNumber, InputBack, InputSince) = (kind, target, number, back, at);
    }

    // Ждёт ли экран текст сейчас
    public bool AwaitsInput(DateTimeOffset at) => InputKind is not null && InputSince is { } since && at - since <= InputLifetime;

    public void ClearInput() => (InputKind, InputTarget, InputNumber, InputBack, InputSince) = (null, null, null, null, null);

    // Черновик правки на проверке: письмо и текстовые ответы анкеты по порядку — до «Одобрить с этим письмом». Один на чат
    public string? DraftVacancyId { get; private set; }

    public List<string> DraftBlocks { get; private set; } = [];

    public void SaveDraft(string vacancyId, IReadOnlyCollection<string> blocks)
    {
        if (string.IsNullOrWhiteSpace(vacancyId) || vacancyId.Length > MaxKey || blocks.Count is 0 or > MaxDraftBlocks || blocks.Any(b => b.Length > MaxDraftBlock))
            throw new DomainException("Черновик не помещается");

        (DraftVacancyId, DraftBlocks) = (vacancyId, [.. blocks]);
    }

    public void ClearDraft() => (DraftVacancyId, DraftBlocks) = (null, []);

    // ── Сводка обычных уведомлений: одно сообщение, в которое новые события дописываются правкой ─────────

    public int? DigestMessageId { get; private set; }

    public string? DigestText { get; private set; }

    public DateTimeOffset? DigestStartedAt { get; private set; }

    // Текст сводки с новой строкой; null — дописать нельзя (сводки нет, устарела, открыта или не помещается) — начать новую
    public string? AppendToDigest(string line, DateTimeOffset at)
    {
        if (DigestMessageId is null || DigestText is null || DigestStartedAt is not { } started || at - started > DigestLifetime)
            return null;

        var text = $"{DigestText}\n{line}";
        return text.Length > MaxDigest ? null : text;
    }

    public void ShowDigest(int messageId, string text, DateTimeOffset at, bool started)
    {
        if (text.Length > MaxDigest)
            throw new DomainException("Сводка не помещается");

        (DigestMessageId, DigestText) = (messageId, text);
        if (started)
            DigestStartedAt = at;
    }

    // Сводку открыли: следующее событие начнёт новую
    public void ResetDigest() => (DigestMessageId, DigestText, DigestStartedAt) = (null, null, null);

    public static readonly TimeSpan InputLifetime = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan DigestLifetime = TimeSpan.FromHours(6);

    // callback_data Telegram — до 64 байт; ключи режима из неё
    public const int MaxKey = 64;

    public const int MaxDraftBlocks = 51;

    public const int MaxDraftBlock = 4000;

    public const int MaxDigest = 3900;


    public static string HashOf(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim())));

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
}
