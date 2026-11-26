using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Dtos;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Queries.Vacancies;

internal static class RegionalVacancyVariants
{
    public static async Task<List<VacancyDto>> EnrichAsync(IAppDbContext db, Guid? owner, List<VacancyDto> items, CancellationToken ct)
    {
        if (items.Count == 0)
            return items;
        var ids = items.Select(item => item.DuplicateOfVacancyId ?? item.Id).Distinct().ToArray();
        var rows = await db.JobApplications.AsNoTracking().Where(application => application.UserId == owner
                && application.DuplicateOfVacancyId != null && ids.Contains(application.DuplicateOfVacancyId))
            .OrderBy(application => application.CreatedAt).ThenBy(application => application.Id)
            .Select(application => new
            {
                Group = application.DuplicateOfVacancyId!,
                Region = new RegionalVacancyDto(application.VacancyId, application.Vacancy.Area, application.Vacancy.Url),
            }).ToListAsync(ct);

        var variants = rows.ToLookup(row => row.Group, row => row.Region);
        return items.Select(item => item with { OtherRegions = variants[item.DuplicateOfVacancyId ?? item.Id].ToList() }).ToList();
    }
}
