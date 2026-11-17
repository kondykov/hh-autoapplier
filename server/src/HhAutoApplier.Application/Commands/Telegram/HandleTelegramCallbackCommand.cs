namespace HhAutoApplier.Application.Commands.Telegram;

// Нажатие кнопки под сообщением бота: Data — «действие:аргументы» (до 64 байт); MessageId — сообщение с кнопкой (экран или уведомление)
public record HandleTelegramCallbackCommand
{
    public required long ChatId { get; init; }

    public required string Data { get; init; }

    public int MessageId { get; init; }
}
