namespace HhAutoApplier.Application.Dtos;

// Состояние привязки Telegram у текущего пользователя
public record TelegramChatDto(bool Configured, bool Linked, string? Username, DateTimeOffset? LinkedAt);

// Код привязки показывается один раз; Link — ссылка на бота с кодом (null — имя бота не получено, код отправить вручную: /start <код>)
public record TelegramLinkDto(string Code, string? Link, DateTimeOffset ExpiresAt);

// Ответ бота: текст и ряды кнопок под ним (Data возвращается боту при нажатии)
public record BotMessage
{
    public BotMessage(string text) => Text = text;

    // Кнопки одним рядом
    public BotMessage(string text, IReadOnlyList<BotButton> buttons) : this(text) => Rows = buttons.Count == 0 ? [] : [buttons];

    public string Text { get; init; }

    public IReadOnlyList<IReadOnlyList<BotButton>> Rows { get; init; } = [];

    public IEnumerable<BotButton> Buttons => Rows.SelectMany(r => r);

    // Клавиатура меню внизу чата; у сообщения с кнопками под ним — не меняется (у сообщения Telegram одна разметка)
    public BotMenu Menu { get; init; }

    // Текст с разметкой HTML Telegram (<b>, <i>, <a>, <pre>); всё, что пришло извне, экранировано (BotHtml)
    public bool Html { get; init; }

    // Попросить ответить на это сообщение (ForceReply) с подсказкой в поле ввода; ответ придёт с ReplyTo. Кнопок у такого сообщения нет
    public string? AskReply { get; init; }

    // Файл вместо текста: Text — подпись
    public BotFile? File { get; init; }
}

public record BotFile(byte[] Content, string Name);

public enum BotMenu
{
    // Не трогать: показанное меню остаётся
    Keep,
    Show,
    Remove,
}

// Url — кнопка-ссылка: открывает адрес, боту ничего не приходит
public record BotButton(string Text, string Data)
{
    public string? Url { get; init; }

    public static BotButton Link(string text, string url) => new(text, "") { Url = url };
}

// Сообщение бота, на которое ответили или под которым нажали кнопку: текст без разметки и блоки <pre> по порядку.
// FromBot — сообщение написал этот бот: только такому тексту можно верить как своему
public record BotSource(string Text, IReadOnlyList<string> Blocks, bool FromBot);

// Нажатие кнопки: Notice — всплывающая подсказка; Status — дописать к сообщению и заменить кнопки на StatusRows
// (null — сообщение не трогать); Edit — заменить сообщение целиком (листание на месте); Replies — новые сообщения в чат
public record BotCallbackResult(string Notice, string? Status)
{
    public IReadOnlyList<IReadOnlyList<BotButton>> StatusRows { get; init; } = [];

    public BotMessage? Edit { get; init; }

    public IReadOnlyList<BotMessage> Replies { get; init; } = [];
}

public record ReportEventResultDto(bool Notified);
