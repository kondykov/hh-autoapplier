using System.Security.Cryptography;
using System.Text.Json;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Vacancies;

// Группируем только подтверждённые удалённые карточки: снэпшот выдачи расширения не содержит полного описания.
internal static class RegionalVacancyDuplicates
{
    public static async Task<bool> GroupAsync(IAppDbContext db, JobApplication application, DateTimeOffset now, CancellationToken ct)
    {
        var vacancy = application.Vacancy;
        if (!vacancy.CanGroupRemote())
            return false;

        var key = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { vacancy.Company, vacancy.Title, vacancy.Description, vacancy.SalaryFrom, vacancy.SalaryTo, vacancy.SalaryCurrency })));
        var original = db.ChangeTracker.Entries<JobApplication>()
            .Select(entry => entry.Entity)
            .FirstOrDefault(candidate => candidate.UserId == application.UserId && candidate.VacancyId != vacancy.Id
                && candidate.RegionalGroupKey == key);
        original ??= await db.JobApplications.Include(candidate => candidate.Vacancy).FirstOrDefaultAsync(candidate => candidate.UserId == application.UserId
            && candidate.VacancyId != vacancy.Id && candidate.RegionalGroupKey == key, ct);
        original ??= await db.JobApplications.Include(candidate => candidate.Vacancy).Where(candidate => candidate.UserId == application.UserId
                && candidate.VacancyId != vacancy.Id && candidate.RegionalGroupKey == null
                && candidate.DuplicateOfVacancyId == null && candidate.Status != VacancyStatus.New
                && !candidate.Vacancy.Imported && candidate.Vacancy.WorkFormat == Vacancy.RemoteWorkFormat
                && candidate.Vacancy.Area != "" && candidate.Vacancy.Area.Trim() != ""
                && candidate.Vacancy.Company == vacancy.Company && candidate.Vacancy.Title == vacancy.Title
                && candidate.Vacancy.Description == vacancy.Description && candidate.Vacancy.SalaryFrom == vacancy.SalaryFrom
                && candidate.Vacancy.SalaryTo == vacancy.SalaryTo && candidate.Vacancy.SalaryCurrency == vacancy.SalaryCurrency)
            .OrderBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id).FirstOrDefaultAsync(ct);

        if (original is null)
        {
            application.KeepAsRegionalOriginal(key);
            return false;
        }

        original.KeepAsRegionalOriginal(key);
        if (original.Vacancy.Area == vacancy.Area)
            return false;
        application.GroupRegionalDuplicate(original, now);
        return true;
    }
}
