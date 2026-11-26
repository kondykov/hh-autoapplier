using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Llm;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Application.Queries.Llm;

public class GetLlmOverviewQueryHandler(
    IAppDbContext db,
    ICurrentUser current,
    ILlmConnections connections,
    IUserService users,
    IOptions<LlmOptions> options,
    TimeProvider clock
) : IQueryHandler<GetLlmOverviewQuery, GetLlmOverviewResult>
{
    public const int BorderlineFrom = 60;

    public const int BorderlineTo = 69;

    public const int PeriodDays = 30;

    public async Task<GetLlmOverviewResult> Handle(GetLlmOverviewQuery query, CancellationToken ct = default)
    {
        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;

        var owner = scope.AsT0;
        var serverWide = current.IsAdmin && query.UserId is null;
        var now = clock.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var day = now.AddHours(-24);
        var since = now.AddDays(-PeriodDays);

        var requests = await Requests(owner, today, ct);
        var calls = db.LlmCalls.Where(c => c.CreatedAt >= day);
        if (!serverWide)
            calls = calls.Where(c => c.UserId == owner);

        var errorKinds = (await calls.Where(c => c.Error != null).Select(c => c.Error).ToListAsync(ct))
            .Select(LlmErrorKinds.Classify).ToList();
        var errors = new LlmErrorsDto(errorKinds.Count, errorKinds.Count(k => k == LlmErrorKinds.Limit), errorKinds.Count(k => k == LlmErrorKinds.Network),
            errorKinds.Count(k => k == LlmErrorKinds.Provider), errorKinds.Count(k => k == LlmErrorKinds.Other));

        var mine = db.JobApplications.Where(a => a.UserId == owner);
        var rejects = new LlmRejectsSummaryDto(
            await mine.CountAsync(a => a.LlmVerdict == LlmVerdict.Reject && a.LlmEvaluatedAt >= since, ct),
            await mine.CountAsync(a => a.LlmVerdict != null && a.LlmEvaluatedAt >= since, ct),
            await mine.CountAsync(a => a.LlmVerdict == LlmVerdict.Reject && a.Status != VacancyStatus.Skipped, ct),
            await mine.CountAsync(a => a.LlmVerdict == LlmVerdict.Reject && a.Status == VacancyStatus.Skipped && a.HiddenAt == null && a.DuplicateOfVacancyId == null
                && a.LlmScore >= BorderlineFrom && a.LlmScore <= BorderlineTo, ct));
        var letters = new LlmLettersSummaryDto(
            await mine.CountAsync(a => a.Status == VacancyStatus.Review && a.StatusReason != null && a.StatusReason.StartsWith(JobApplication.LetterReviewPrefix), ct),
            await mine.CountAsync(a => a.CoverLetterAt >= since && a.CoverLetter != null, ct));

        var routes = await Routes(owner, calls, ct);
        var attention = await Attention(routes, requests, rejects, letters, ct);
        return new LlmOverviewDto(requests, errors, rejects, letters, routes, attention);
    }

    private async Task<LlmRequestsDto> Requests(Guid? owner, DateTimeOffset today, CancellationToken ct)
    {
        // Лимит расходуют только запросы, дошедшие до модели (как LlmUserQuota)
        var used = await db.LlmCalls.Where(c => c.CreatedAt >= today && c.Model != "")
            .GroupBy(c => c.UserId).Select(g => new { UserId = g.Key, Count = g.Count() }).ToListAsync(ct);
        List<LlmUserRequestsDto>? byUser = null;
        if (current.IsAdmin)
        {
            var names = (await users.GetAllAsync(ct)).ToDictionary(u => u.Id, u => u.Email);
            byUser = used.OrderByDescending(u => u.Count)
                .Select(u => new LlmUserRequestsDto(u.UserId, u.UserId is { } id && names.TryGetValue(id, out var name) ? name : "сервер", u.Count))
                .ToList();
        }

        var opt = options.Value;
        return new LlmRequestsDto(used.Sum(u => u.Count), opt.DailyRequestLimit, used.Where(u => u.UserId == owner).Sum(u => u.Count),
            opt.UserDailyRequestLimit, byUser);
    }

    private async Task<List<LlmRouteDto>> Routes(Guid? owner, IQueryable<LlmCall> calls, CancellationToken ct)
    {
        var settings = await db.UserLlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == owner, ct);
        var byPurpose = await calls.GroupBy(c => c.Purpose)
            .Select(g => new { Purpose = g.Key, Count = g.Count(), Errors = g.Count(c => c.Error != null) }).ToListAsync(ct);
        var named = connections.Named();
        return LlmTasks.All.Select(task =>
        {
            var choice = settings?.ConnectionFor(task);
            var connection = named.FirstOrDefault(c => string.Equals(c.Name, choice, StringComparison.OrdinalIgnoreCase)) ?? connections.DefaultFor(task);
            var purposes = LlmRoutes.PurposesOf(task).ToHashSet();
            var stats = byPurpose.Where(p => purposes.Contains(p.Purpose)).ToList();
            return new LlmRouteDto(task, choice, ToDto(connection), stats.Sum(s => s.Count), stats.Sum(s => s.Errors));
        }).ToList();
    }

    private async Task<List<LlmAttentionDto>> Attention(List<LlmRouteDto> routes, LlmRequestsDto requests, LlmRejectsSummaryDto rejects,
        LlmLettersSummaryDto letters, CancellationToken ct)
    {
        var items = new List<LlmAttentionDto>();
        var paused = routes.Select(r => r.Connection).Concat(connections.Named().Select(ToDto))
            .Where(c => c.PausedUntil is not null).DistinctBy(c => (c.Name, c.Model));
        foreach (var c in paused)
            items.Add(new LlmAttentionDto("pause", $"{c.Name} ({c.Model}) на паузе до {MoscowTime.Format(c.PausedUntil!.Value)}: {c.PauseReason}", null));

        var outages = await db.LinkOutages.AsNoTracking().Where(o => o.EndedAt == null).OrderBy(o => o.StartedAt).ToListAsync(ct);
        foreach (var o in outages)
            items.Add(new LlmAttentionDto("link", $"Нет связи: {o.Subject} — {o.Reason} с {MoscowTime.Format(o.StartedAt)}{(o.Detail is null ? "" : $" ({o.Detail})")}", "/system"));

        if (requests.Limit > 0 && requests.Server >= requests.Limit)
            items.Add(new LlmAttentionDto("limit", $"Дневной лимит сервера {requests.Limit} исчерпан: запросы продолжатся после 03:00 МСК", null));
        else if (requests.UserLimit > 0 && requests.Own >= requests.UserLimit)
            items.Add(new LlmAttentionDto("limit", $"Доля запросов {requests.UserLimit} за сутки исчерпана: запросы продолжатся после 03:00 МСК", null));

        if (rejects.Borderline > 0)
            items.Add(new LlmAttentionDto("rejects", $"{rejects.Borderline} отсеянных с баллом {BorderlineFrom}–{BorderlineTo} — пограничные, стоит просмотреть",
                "/llm/rejects?border=1"));

        if (letters.Rejected > 0)
            items.Add(new LlmAttentionDto("letters", $"Проверка отклонила письма: {letters.Rejected} ждут решения", "/review"));

        return items;
    }

    private static LlmConnectionDto ToDto(LlmConnectionStatus s) =>
        new(s.Name, s.Provider, s.Model, s.Available, s.PausedUntil, s.PauseReason, s.UsedToday, s.DailyLimit);
}
