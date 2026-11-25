using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Applications;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Queries.Vacancies;

public class GetVacancyQueryHandler(
    IAppDbContext db,
    IExtractor<JobApplication, VacancyDto> extractor,
    ICurrentUser current
) : IQueryHandler<GetVacancyQuery, GetVacancyResult>
{
    public async Task<GetVacancyResult> Handle(GetVacancyQuery query, CancellationToken ct = default)
    {
        var application = await db.Applications(current.Id).AsNoTracking().FirstOrDefaultAsync(a => a.VacancyId == query.VacancyId, ct);
        if (application is null)
            return new NotFoundError($"Вакансия {query.VacancyId} не найдена");
        var cards = await RegionalVacancyVariants.EnrichAsync(db, current.Id, [extractor.Extract(application)], ct);
        return cards[0];
    }
}
