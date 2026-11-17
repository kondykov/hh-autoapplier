namespace HhAutoApplier.Application.Commands.Telegram;

// Транспорт прислал экран или сводку новым сообщением — запомнить его id, чтобы дальше править на месте (#115)
public record RememberTelegramScreenCommand(long ChatId, int MessageId);
