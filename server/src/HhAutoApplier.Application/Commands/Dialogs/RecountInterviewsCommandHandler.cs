using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Domain.Dialogs;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Dialogs;

// Возвращает число откликов, переведённых в Interview. Время перехода — время сообщения работодателя:
// так собеседование попадает в свой период дашборда, а не в день обновления
public class RecountInterviewsCommandHandler(IAppDbContext db) : ICommandHandler<RecountInterviewsCommand, int>
{
    public const string Reason = "собеседование (чат hh.ru, пересчёт)";

    public async Task<int> Handle(RecountInterviewsCommand command, CancellationToken ct = default)
    {
        var messages = await db.EmployerMessages.AsNoTracking()
            .Where(m => m.HhState == EmployerMessage.Interview && m.VacancyId != null)
            .Select(m => new { m.UserId, VacancyId = m.VacancyId!, m.At })
            .ToListAsync(ct);
        if (messages.Count == 0)
            return 0;

        var vacancyIds = messages.Select(m => m.VacancyId).Distinct().ToList();
        var applications = await db.JobApplications
            .Where(a => vacancyIds.Contains(a.VacancyId) && (a.Status == VacancyStatus.Applied || a.Status == VacancyStatus.Invited))
            .ToListAsync(ct);

        var byChat = messages.GroupBy(m => (m.UserId, m.VacancyId)).ToDictionary(g => g.Key, g => g.Max(m => m.At));
        var moved = 0;
        foreach (var application in applications)
        {
            if (application.UserId is not { } userId || !byChat.TryGetValue((userId, application.VacancyId), out var at))
                continue;

            application.ScheduleInterview(Reason, at);
            moved++;
        }

        if (moved > 0)
            await db.SaveChangesAsync(ct);
        return moved;
    }
}
