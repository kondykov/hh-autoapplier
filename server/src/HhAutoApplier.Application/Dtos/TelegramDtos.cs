namespace HhAutoApplier.Application.Dtos;

// Состояние привязки Telegram у текущего пользователя
public record TelegramChatDto(bool Configured, bool Linked, string? Username, DateTimeOffset? LinkedAt);

// Код привязки показывается один раз; Link — ссылка на бота с кодом (null — имя бота не получено, код отправить вручную: /start <код>)
public record TelegramLinkDto(string Code, string? Link, DateTimeOffset ExpiresAt);

// Сообщение бота: текст и ряды кнопок под ним (Data возвращается боту при нажатии)
public record BotMessage
{
    public BotMessage(string text) => Text = text;

    // Кнопки одним рядом
    public BotMessage(string text, IReadOnlyList<BotButton> buttons) : this(text) => Rows = buttons.Count == 0 ? [] : [buttons];

    public string Text { get; init; }

    public IReadOnlyList<IReadOnlyList<BotButton>> Rows { get; init; } = [];

    public IEnumerable<BotButton> Buttons => Rows.SelectMany(r => r);

    // Текст с разметкой HTML Telegram (<b>, <i>, <a>, <pre>, <blockquote>); всё, что пришло извне, экранировано (BotHtml)
    public bool Html { get; init; }

    // Убрать клавиатуру-меню внизу чата, оставшуюся от прежних версий бота (#115: меню — экран и команда /menu)
    public bool RemoveKeyboard { get; init; }

    // Служебное сообщение: удалить сразу после отправки (так убирается клавиатура, не оставляя следа в чате)
    public bool DeleteAfterSend { get; init; }

    // Файл вместо текста: Text — подпись
    public BotFile? File { get; init; }
}

public record BotFile(byte[] Content, string Name);

// Url — кнопка-ссылка: открывает адрес, боту ничего не приходит
public record BotButton(string Text, string Data)
{
    public string? Url { get; init; }

    public static BotButton Link(string text, string url) => new(text, "") { Url = url };
}

// Ответ бота на сообщение или нажатие (#115, «один экран»): что сделать с чатом
public record BotResult
{
    // Всплывающая подсказка нажатия — подтверждения вместо новых сообщений
    public string Notice { get; init; } = "";

    // Перерисовать экран: сообщение ScreenMessageId; null или не правится — прислать новый и запомнить
    public BotMessage? Screen { get; init; }

    public int? ScreenMessageId { get; init; }

    // Экран — заново внизу чата: прежний (ScreenMessageId) удалить
    public bool MoveScreen { get; init; }

    // Свернуть нажатое сообщение (уведомление) до первой строки и итога; кнопки — StatusRows
    public string? Status { get; init; }

    public IReadOnlyList<IReadOnlyList<BotButton>> StatusRows { get; init; } = [];

    // Удалить нажатое сообщение («Понятно» под уведомлением)
    public bool DeletePressed { get; init; }

    // Удалить сообщения: команду и введённый текст пользователя
    public IReadOnlyList<int> Delete { get; init; } = [];

    // Отдельные сообщения: файл, ответ на привязку, служебное снятие клавиатуры
    public IReadOnlyList<BotMessage> Messages { get; init; } = [];

    // Меню команд этого чата (#118): Full — после привязки, Stranger — после отвязки; null — не трогать
    public BotCommandsMenu? CommandsMenu { get; init; }
}

public enum BotCommandsMenu
{
    Stranger,
    Full,
}

public record ReportEventResultDto(bool Notified);
