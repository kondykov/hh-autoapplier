using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Applications;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Queries.Vacancies;

public class GetVacanciesQueryHandler(
    IAppDbContext db,
    IExtractor<JobApplication, VacancyDto> extractor,
    ICurrentUser current
) : IQueryHandler<GetVacanciesQuery, GetVacanciesResult>
{
    public const int MinTextLength = 2;

    public const int TextLimit = 20;

    public async Task<GetVacanciesResult> Handle(GetVacanciesQuery query, CancellationToken ct = default)
    {
        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;

        var q = db.Applications(scope.AsT0).AsNoTracking().Where(application => application.DuplicateOfVacancyId == null);
        if (query.Status is not null)
            q = q.Where(a => a.Status == query.Status);

        if (!query.IncludeHidden)
            q = q.Where(a => a.HiddenAt == null);

        if (!string.IsNullOrWhiteSpace(query.Search))
            q = q.Where(a => a.Search != null && a.Search.Name == query.Search);

        var take = Math.Clamp(query.Take, 1, 500);
        if (query.Q is not null)
        {
            var text = query.Q.Trim();
            if (text.Length < MinTextLength)
                return new List<VacancyDto>();

            // Каждое слово — в названии или в компании. Индекса нет: вакансий у пользователя сотни (#141)
            foreach (var word in text.ToLowerInvariant().Replace('ё', 'е').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                q = q.Where(a => a.Vacancy.Title.ToLower().Replace("ё", "е").Contains(word)
                    || a.Vacancy.Company.ToLower().Replace("ё", "е").Contains(word));
            take = Math.Min(take, TextLimit);
        }

        var rows = await q.OrderByDescending(a => a.Vacancy.PublishedAt)
            .Skip(Math.Max(query.Skip, 0)).Take(take).ToListAsync(ct);
        return await RegionalVacancyVariants.EnrichAsync(db, scope.AsT0, rows.Select(extractor.Extract).ToList(), ct);
    }
}
