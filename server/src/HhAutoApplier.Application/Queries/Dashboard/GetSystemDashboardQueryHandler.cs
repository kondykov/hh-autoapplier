using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.About;
using HhAutoApplier.Application.Queries.Collecting;
using HhAutoApplier.Application.Queries.Llm;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Queries.Dashboard;

// Сборка и сбор — из своих запросов, LLM сервера — обзор вкладки LLM (#127) без ?userId; здесь — пользователи и уведомления
public class GetSystemDashboardQueryHandler(
    IAppDbContext db,
    ICurrentUser current,
    IUserService users,
    GetVersionQueryHandler version,
    GetCollectionStateQueryHandler collection,
    GetLlmOverviewQueryHandler llm,
    TimeProvider clock
) : IQueryHandler<GetSystemDashboardQuery, GetSystemDashboardResult>
{
    public async Task<GetSystemDashboardResult> Handle(GetSystemDashboardQuery query, CancellationToken ct = default)
    {
        if (!current.IsAdmin)
            return new ForbiddenError("Система — только администратору");

        if (!DashboardMath.Periods.Contains(query.Period))
            return new ValidationError($"Период — {string.Join(", ", DashboardMath.Periods)} дней");

        var since = clock.GetUtcNow().AddDays(-query.Period);
        var overview = (await llm.Handle(new GetLlmOverviewQuery(), ct)).AsT0;

        var collected = await db.JobApplications.Where(a => a.CreatedAt >= since && a.UserId != null)
            .GroupBy(a => a.UserId!.Value).Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.UserId, g => g.Count, ct);
        var sent = await db.JobApplications.Where(a => a.AppliedAt != null && a.UserId != null)
            .GroupBy(a => a.UserId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count(a => a.AppliedAt >= since), Last = g.Max(a => a.AppliedAt) })
            .ToDictionaryAsync(g => g.UserId, ct);
        var calls = await db.LlmCalls.Where(c => c.CreatedAt >= since && c.UserId != null)
            .GroupBy(c => c.UserId!.Value)
            .Select(g => new
            {
                UserId = g.Key,
                Requests = g.Count(c => c.Model != ""),
                Errors = g.Count(c => c.Error != null),
                Tokens = g.Sum(c => (long)c.PromptTokens + c.CompletionTokens),
            })
            .ToDictionaryAsync(g => g.UserId, ct);
        var chats = await db.EmployerMessages.GroupBy(m => m.UserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(m => m.ReceivedAt) })
            .ToDictionaryAsync(g => g.UserId, g => g.Last, ct);

        var list = (await users.GetAllAsync(ct)).Select(u =>
            {
                var s = sent.GetValueOrDefault(u.Id);
                var c = calls.GetValueOrDefault(u.Id);
                return new SystemUserDto(u.Id, u.Email, u.Role, u.Blocked, collected.GetValueOrDefault(u.Id), s?.Count ?? 0,
                    c?.Requests ?? 0, c?.Errors ?? 0, c?.Tokens ?? 0, s?.Last, chats.TryGetValue(u.Id, out var at) ? at : null);
            })
            .OrderByDescending(u => u.Sent).ThenByDescending(u => u.Collected).ThenBy(u => u.Email).ToList();

        var unsent = db.Notifications.Where(n => n.SentAt == null);
        var notifications = new SystemNotificationsDto(await unsent.CountAsync(ct), await unsent.CountAsync(n => n.Error != null, ct),
            await unsent.MinAsync(n => (DateTimeOffset?)n.CreatedAt, ct));

        return new SystemDashboardDto(query.Period, await version.Handle(new GetVersionQuery(), ct),
            await collection.Handle(new GetCollectionStateQuery(), ct), overview, list, notifications);
    }
}
