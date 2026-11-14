using System.ComponentModel.DataAnnotations;

namespace HhAutoApplier.Application.Commands.Notifications;

public enum ExtensionEvent
{
    // hh.ru показал капчу: расширение встало на паузу и ждёт человека
    Captcha = 0,
    // Исчерпан дневной лимит откликов
    DailyLimit = 1,
    // Слетел вход в hh.ru: очередь остановлена, отклики остались в очереди (#95)
    SignedOut = 2,
}

// Событие расширения — в Telegram владельцу токена. Повторы гасятся: капча — одно сообщение в час, лимит — в сутки
public record ReportEventCommand
{
    [Required]
    public required ExtensionEvent Kind { get; init; }

    [MaxLength(500)]
    public string? Message { get; init; }
}
