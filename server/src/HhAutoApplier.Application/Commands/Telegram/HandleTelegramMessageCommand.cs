namespace HhAutoApplier.Application.Commands.Telegram;

// Сообщение боту. Отвечает только в личном чате: в группе ответы видели бы посторонние.
// MessageId — само сообщение: команду и введённый текст бот удаляет, чтобы в чате оставались экран и уведомления (#115)
public record HandleTelegramMessageCommand
{
    public required long ChatId { get; init; }

    public bool IsPrivate { get; init; }

    public required string Text { get; init; }

    // @username отправителя — запоминается при привязке
    public string? Username { get; init; }

    public int MessageId { get; init; }
}
