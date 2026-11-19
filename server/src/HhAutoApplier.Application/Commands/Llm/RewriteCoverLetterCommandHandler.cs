using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Llm;

public class RewriteCoverLetterCommandHandler(IAppDbContext db, ICurrentUser current, WriteCoverLettersCommandHandler writer)
    : ICommandHandler<RewriteCoverLetterCommand, RewriteCoverLetterResult>
{
    public async Task<RewriteCoverLetterResult> Handle(RewriteCoverLetterCommand command, CancellationToken ct = default)
    {
        // Только своё и только письмо, отклонённое проверкой: остальные письма переписывает «Сбросить письма» администратора
        var application = await db.AllApplications()
            .SingleOrDefaultAsync(a => a.VacancyId == command.VacancyId && a.UserId == current.Id && a.Status == VacancyStatus.Review, ct);
        if (application is null || !application.LetterRejected)
            return new NotFoundError($"Письмо, отклонённое проверкой, для вакансии {command.VacancyId} не найдено");

        // Уже переписанное LLM — заново; поправленное человеком не трогаем
        if (application.CoverLetter is not null)
        {
            if (application.CoverLetterModel == JobApplication.ManualLetter)
                return new ConflictError("Письмо уже поправлено вручную — правьте его в «Проверке»");

            application.ResetCoverLetter();
            await db.SaveChangesAsync(ct);
        }

        var result = await writer.Handle(new WriteCoverLettersCommand { Limit = 1, VacancyId = application.VacancyId, UserId = application.UserId }, ct);
        return result.Match<RewriteCoverLetterResult>(r => r, e => e);
    }
}
