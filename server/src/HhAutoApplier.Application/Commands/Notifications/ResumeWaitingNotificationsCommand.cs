namespace HhAutoApplier.Application.Commands.Notifications;

// Проверка связи увидела Telegram в TelegramOkAt (#132): дослать уведомления, ждавшие связи до этого момента
public record ResumeWaitingNotificationsCommand(DateTimeOffset TelegramOkAt);

// Resumed — важные снова в очереди отправки; Digested — обычные, вошедшие в сводки пропущенного; Expired — старше суток
public record ResumeWaitingNotificationsResult(int Resumed, int Digested, int Expired);
