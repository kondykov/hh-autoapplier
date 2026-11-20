using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using OneOf;

namespace HhAutoApplier.Application.Queries.Llm;

// «LLM → Письма» (#127): письма LLM, отклонённые проверкой. Отдельных полей нет: отклик на проверке с причиной
// «письмо: проверить — …» (RequireLetterReview), черновик — последний ответ LLM на письмо этой вакансии в журнале.
// Свои; администратор — ?userId
public record GetLlmLettersQuery(Guid? UserId = null);

// На .NET 11: public union GetLlmLettersResult(IReadOnlyList<LlmLetterDto>, ForbiddenError);
[GenerateOneOf]
public partial class GetLlmLettersResult : OneOfBase<List<LlmLetterDto>, ForbiddenError>;

public class GetLlmLettersQueryHandler(IAppDbContext db, ICurrentUser current) : IQueryHandler<GetLlmLettersQuery, GetLlmLettersResult>
{
    public async Task<GetLlmLettersResult> Handle(GetLlmLettersQuery query, CancellationToken ct = default)
    {
        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;

        var owner = scope.AsT0;
        var rejected = await db.JobApplications.AsNoTracking()
            .Where(a => a.UserId == owner && a.Status == VacancyStatus.Review && a.StatusReason != null && a.StatusReason.StartsWith(JobApplication.LetterReviewPrefix))
            .Select(a => new { a.VacancyId, a.Vacancy.Title, a.Vacancy.Company, a.Vacancy.Url, a.StatusReason, a.CoverLetter, a.CoverLetterModel, a.CoverLetterAt })
            .ToListAsync(ct);
        if (rejected.Count == 0)
            return new List<LlmLetterDto>();

        var ids = rejected.Select(r => r.VacancyId).ToList();
        var drafts = (await db.LlmCalls.AsNoTracking()
                .Where(c => c.Purpose == CoverLetterWriting.Purpose && c.UserId == owner && c.Response != null && c.VacancyId != null && ids.Contains(c.VacancyId))
                .Select(c => new { c.Id, c.VacancyId, c.Model, c.Response, c.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(c => c.VacancyId!).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).First());

        return rejected.Select(r =>
        {
            var draft = drafts.GetValueOrDefault(r.VacancyId);
            var rewritten = r.CoverLetter is not null;
            return new LlmLetterDto(r.VacancyId, r.Title, r.Company, r.Url, Reason(r.StatusReason!),
                rewritten ? r.CoverLetter : draft is null ? null : CoverLetterWriting.Parse(draft.Response!) ?? draft.Response,
                rewritten ? r.CoverLetterModel : draft?.Model, rewritten, rewritten ? r.CoverLetterAt : draft?.CreatedAt);
        }).OrderByDescending(l => l.At).ToList();
    }

    // «письмо: проверить — цифры не из фактов: 15» → «цифры не из фактов: 15»
    public static string Reason(string statusReason)
    {
        var reason = statusReason[JobApplication.LetterReviewPrefix.Length..].TrimStart(' ', '—', '-', ':');
        return reason.Length > 0 ? reason : "без причины";
    }
}
