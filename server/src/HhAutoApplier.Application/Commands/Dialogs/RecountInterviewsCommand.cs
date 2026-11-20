namespace HhAutoApplier.Application.Commands.Dialogs;

// Пересчёт собеседований (#122): до статуса Interview состояние INTERVIEW из чата hh.ru давало Invited.
// Сохранённое сообщение работодателя с INTERVIEW переводит отправленный отклик (Applied, Invited) в Interview.
// Идемпотентен: отклик в Interview, отказ и прочие статусы не трогает; запускается при старте приложения
public record RecountInterviewsCommand;
