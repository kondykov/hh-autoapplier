using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Dialogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Application.Queries.Dashboard;

public class GetDashboardQueryHandler(IAppDbContext db, ICurrentUser current, IOptions<LlmOptions> options, TimeProvider clock)
    : IQueryHandler<GetDashboardQuery, GetDashboardResult>
{
    // Агрегатор вакансии (#32): пока поля источника нет, все отклики — с hh. Со вторым источником — поле вакансии в выборке
    public const string HhSource = "hh";

    public async Task<GetDashboardResult> Handle(GetDashboardQuery query, CancellationToken ct = default)
    {
        if (!DashboardMath.Periods.Contains(query.Period))
            return new ValidationError($"Период — {string.Join(", ", DashboardMath.Periods)} дней");

        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;

        var owner = scope.AsT0;
        var now = clock.GetUtcNow();
        var prevFrom = now.AddDays(-2 * query.Period);
        // Собранные за два периода (этот и прошлый) и все отправленные: ответ на старый отклик — тоже событие периода
        var rows = await db.JobApplications.AsNoTracking()
            .Where(a => a.UserId == owner && (a.CreatedAt >= prevFrom || a.AppliedAt != null))
            .Select(a => new DashboardRow(a.CreatedAt, a.Status, a.StatusReason, a.LlmScore, a.AppliedAt,
                a.Search != null ? a.Search.Name : a.Vacancy.SearchName, HhSource, a.History,
                a.Vacancy.ResponsesAtCollect, a.ResponsesAtApply))
            .ToListAsync(ct);

        var source = string.IsNullOrWhiteSpace(query.Source) ? DashboardMath.AllSources : query.Source.Trim().ToLowerInvariant();
        return DashboardMath.Build(rows, query.Period, source, now, await Llm(owner, now, ct), await Chats(owner, ct));
    }

    private async Task<DashboardLlmDto> Llm(Guid? owner, DateTimeOffset now, CancellationToken ct)
    {
        // Сутки лимита — с полуночи UTC (03:00 МСК), как LlmUserQuota; лимит расходуют только запросы, дошедшие до модели
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var calls = db.LlmCalls.Where(c => c.UserId == owner && c.CreatedAt >= today);
        return new DashboardLlmDto(await calls.CountAsync(c => c.Model != "", ct), await calls.CountAsync(c => c.Error != null, ct),
            options.Value.UserDailyRequestLimit);
    }

    private async Task<DashboardChatsDto> Chats(Guid? owner, CancellationToken ct)
    {
        if (owner is not { } userId)
            return new DashboardChatsDto(null, 0);

        var messages = db.EmployerMessages.Where(m => m.UserId == userId);
        var actionMessages = await messages.Where(m => m.ExternalLinks.Any(link => link.State == EmployerLinkState.Pending
            && link.RequiresAction != false)).AsNoTracking().ToListAsync(ct);
        var pending = actionMessages.Sum(m => m.ExternalLinks.Count(link => link.State == EmployerLinkState.Pending && link.RequiresAction != false));
        return new DashboardChatsDto(await messages.MaxAsync(m => (DateTimeOffset?)m.ReceivedAt, ct), await messages.CountAsync(ct), pending);
    }
}
