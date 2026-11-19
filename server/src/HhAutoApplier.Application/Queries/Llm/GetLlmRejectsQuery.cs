using System.ComponentModel.DataAnnotations;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using OneOf;

namespace HhAutoApplier.Application.Queries.Llm;

// «LLM → Отсев» (#127): отсеянные LLM (вердикт Reject, статус Skipped, не скрытые) за период, группы по причине и страница списка.
// Period — дней (7, 30) или null — всё; Group — id из LlmRejectGroups; Border — только пограничные 60–69. Счётчики групп — с учётом периода и Border
public record GetLlmRejectsQuery
{
    [Range(1, 3650)]
    public int? Period { get; init; }

    [MaxLength(16)]
    public string? Group { get; init; }

    public bool Border { get; init; }

    [Range(0, int.MaxValue)]
    public int Skip { get; init; }

    [Range(1, 500)]
    public int Take { get; init; } = 100;

    public Guid? UserId { get; init; }
}

// На .NET 11: public union GetLlmRejectsResult(LlmRejectsDto, ForbiddenError);
[GenerateOneOf]
public partial class GetLlmRejectsResult : OneOfBase<LlmRejectsDto, ForbiddenError>;

public class GetLlmRejectsQueryHandler(IAppDbContext db, ICurrentUser current, TimeProvider clock) : IQueryHandler<GetLlmRejectsQuery, GetLlmRejectsResult>
{
    public async Task<GetLlmRejectsResult> Handle(GetLlmRejectsQuery query, CancellationToken ct = default)
    {
        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;

        var owner = scope.AsT0;
        var mine = db.JobApplications.AsNoTracking().Where(a => a.UserId == owner && a.LlmVerdict == LlmVerdict.Reject);
        var rejected = mine.Where(a => a.Status == VacancyStatus.Skipped && a.HiddenAt == null);
        if (query.Period is { } days)
        {
            var since = clock.GetUtcNow().AddDays(-days);
            rejected = rejected.Where(a => a.LlmEvaluatedAt >= since);
        }

        // Отсеянных — сотни: группы по ключевым словам считаются в памяти
        var rows = await rejected
            .Select(a => new { a.VacancyId, a.Vacancy.Title, a.Vacancy.Company, a.Vacancy.Url, a.LlmScore, a.LlmReason, a.LlmEvaluatedAt })
            .ToListAsync(ct);
        var items = rows.Select(r => new LlmRejectItemDto(r.VacancyId, r.Title, r.Company, r.Url, r.LlmScore, r.LlmReason, LlmRejectGroups.Classify(r.LlmReason),
            r.LlmEvaluatedAt)).ToList();

        var total = items.Count;
        var borderline = items.Count(IsBorderline);
        if (query.Border)
            items = items.Where(IsBorderline).ToList();

        var groups = LlmRejectGroups.Titles.Select(t => new LlmRejectGroupDto(t.Id, t.Title, items.Count(i => i.Group == t.Id))).ToList();
        if (LlmRejectGroups.Exists(query.Group))
            items = items.Where(i => i.Group == query.Group).ToList();

        var page = items.OrderByDescending(i => i.Score ?? 0).ThenByDescending(i => i.EvaluatedAt).ThenBy(i => i.Id)
            .Skip(query.Skip).Take(query.Take).ToList();

        var returned = await mine.Where(a => a.Status != VacancyStatus.Skipped).GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        var summary = new LlmReturnedDto(returned.Sum(r => r.Count),
            returned.Where(r => r.Status is VacancyStatus.Applied or VacancyStatus.Invited or VacancyStatus.Rejected).Sum(r => r.Count),
            returned.Where(r => r.Status == VacancyStatus.Invited).Sum(r => r.Count));
        return new LlmRejectsDto(total, borderline, groups, page, items.Count, summary);
    }

    private static bool IsBorderline(LlmRejectItemDto i) =>
        i.Score is >= GetLlmOverviewQueryHandler.BorderlineFrom and <= GetLlmOverviewQueryHandler.BorderlineTo;
}
