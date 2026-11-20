using System.ComponentModel.DataAnnotations;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Llm;
using Microsoft.EntityFrameworkCore;
using OneOf;

namespace HhAutoApplier.Application.Queries.Llm;

// «LLM → Журнал» (#127): вызовы от новых к старым, повторы одной ошибки подряд свёрнуты. Пользователь — свои вызовы;
// администратор без ?userId — все вызовы сервера (с пользователем), с ?userId — вызовы этого пользователя.
// Purpose — назначение (vacancy-evaluation, cover-letter, …), Model — модель как в журнале, Errors — только с ошибкой
public record GetLlmCallsQuery
{
    [MaxLength(64)]
    public string? Purpose { get; init; }

    [MaxLength(200)]
    public string? Model { get; init; }

    public bool Errors { get; init; }

    [Range(0, int.MaxValue)]
    public int Skip { get; init; }

    [Range(1, 500)]
    public int Take { get; init; } = 100;

    public Guid? UserId { get; init; }
}

// На .NET 11: public union GetLlmCallsResult(LlmCallsDto, ForbiddenError);
[GenerateOneOf]
public partial class GetLlmCallsResult : OneOfBase<LlmCallsDto, ForbiddenError>;

public class GetLlmCallsQueryHandler(IAppDbContext db, ICurrentUser current, IUserService users) : IQueryHandler<GetLlmCallsQuery, GetLlmCallsResult>
{
    private const int MaxModels = 12;

    public async Task<GetLlmCallsResult> Handle(GetLlmCallsQuery query, CancellationToken ct = default)
    {
        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;

        var serverWide = current.IsAdmin && query.UserId is null;
        var owner = scope.AsT0;
        var visible = db.LlmCalls.AsNoTracking();
        if (!serverWide)
            visible = visible.Where(c => c.UserId == owner);

        var calls = visible;
        if (!string.IsNullOrWhiteSpace(query.Purpose))
            calls = calls.Where(c => c.Purpose == query.Purpose);
        if (query.Model is not null)
            calls = calls.Where(c => c.Model == query.Model);
        if (query.Errors)
            calls = calls.Where(c => c.Error != null);

        // Без промпта и ответа: свёртка идёт по всем строкам фильтра (тысячи коротких строк), текст — по клику
        var rows = await calls.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Select(c => new LlmJournal.Row(c.Id, c.Purpose, c.Model, c.Connection, c.Error, c.Response != null, c.PromptTokens, c.CompletionTokens,
                c.UserId, c.VacancyId, c.CreatedAt))
            .ToListAsync(ct);
        var entries = LlmJournal.Collapse(rows);
        var page = entries.Skip(query.Skip).Take(query.Take).ToList();

        // Итог оценки — из ответа, только для строк страницы
        var evaluated = page.Where(e => e.Outcome == LlmJournal.Ok && e.Last.Purpose == VacancyAssessment.Purpose).Select(e => e.Last.Id).ToList();
        var responses = evaluated.Count == 0
            ? new Dictionary<long, string?>()
            : await db.LlmCalls.AsNoTracking().Where(c => evaluated.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Response, ct);

        var vacancyIds = page.Select(e => e.Last.VacancyId).OfType<string>().Distinct().ToList();
        var titles = vacancyIds.Count == 0
            ? new Dictionary<string, string>()
            : await db.Vacancies.AsNoTracking().Where(v => vacancyIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Title, ct);
        var names = serverWide ? (await users.GetAllAsync(ct)).ToDictionary(u => u.Id, u => u.Email) : null;

        var items = page.Select(e =>
        {
            var r = e.Last;
            var detail = e.Outcome == LlmJournal.Ok ? LlmJournal.Verdict(r.Purpose, responses.GetValueOrDefault(r.Id)) : r.Error;
            var user = names is null ? null : r.UserId is { } id && names.TryGetValue(id, out var name) ? name : "сервер";
            return new LlmCallEntryDto(r.Id, r.At, e.Since, e.Count, e.Purposes, r.Model, r.Connection, e.Outcome, detail,
                r.Error is null ? null : LlmErrorKinds.Classify(r.Error), r.PromptTokens, r.CompletionTokens, user, r.VacancyId,
                r.VacancyId is { } v ? titles.GetValueOrDefault(v) : null);
        }).ToList();

        var purposes = await visible.GroupBy(c => c.Purpose).Select(g => new LlmFacetDto(g.Key, g.Count())).ToListAsync(ct);
        var models = await visible.Where(c => c.Model != "").GroupBy(c => c.Model).OrderByDescending(g => g.Count()).Take(MaxModels)
            .Select(g => new LlmFacetDto(g.Key, g.Count())).ToListAsync(ct);
        return new LlmCallsDto(rows.Count, rows.Count(r => r.Error is not null), items, entries.Count,
            purposes.OrderByDescending(p => p.Count).ToList(), models);
    }
}

// Вызов целиком (#127): свой — любому; чужой и записи сервера без пользователя — только администратору, иначе «не найден»
public record GetLlmCallQuery(long Id);

// На .NET 11: public union GetLlmCallResult(LlmCallDto, NotFoundError);
[GenerateOneOf]
public partial class GetLlmCallResult : OneOfBase<LlmCallDto, NotFoundError>;

public class GetLlmCallQueryHandler(IAppDbContext db, ICurrentUser current) : IQueryHandler<GetLlmCallQuery, GetLlmCallResult>
{
    public async Task<GetLlmCallResult> Handle(GetLlmCallQuery query, CancellationToken ct = default)
    {
        var calls = db.LlmCalls.AsNoTracking().Where(c => c.Id == query.Id);
        if (!current.IsAdmin)
            calls = calls.Where(c => c.UserId != null && c.UserId == current.Id);

        var c = await calls.SingleOrDefaultAsync(ct);
        if (c is null)
            return new NotFoundError($"Вызов LLM {query.Id} не найден");

        return new LlmCallDto(c.Id, c.CreatedAt, c.Purpose, c.Model, c.Connection, c.Error, c.Prompt, c.Response, c.PromptTokens, c.CompletionTokens, c.VacancyId);
    }
}
