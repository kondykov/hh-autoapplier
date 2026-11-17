using HhAutoApplier.Application.Dtos;

namespace HhAutoApplier.Application.Commands.Telegram;

// Нажатие кнопки под сообщением бота: Data — «действие:аргументы» (BotTexts, до 64 байт)
public record HandleTelegramCallbackCommand
{
    public required long ChatId { get; init; }

    public required string Data { get; init; }

    // Сообщение с кнопкой: у черновика письма (#115) в нём поправленный текст
    public BotSource? Source { get; init; }
}
