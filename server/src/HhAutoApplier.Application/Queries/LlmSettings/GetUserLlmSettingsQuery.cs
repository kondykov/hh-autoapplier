using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Llm;
using Microsoft.EntityFrameworkCore;
using OneOf;

namespace HhAutoApplier.Application.Queries.LlmSettings;

// Свои модели LLM по задачам (#107); администратор — пользователя UserId (только просмотр)
public record GetUserLlmSettingsQuery(Guid? UserId = null);

// На .NET 11: public union GetUserLlmSettingsResult(UserLlmSettingsDto, ForbiddenError);
[GenerateOneOf]
public partial class GetUserLlmSettingsResult : OneOfBase<UserLlmSettingsDto, ForbiddenError>;

public class GetUserLlmSettingsQueryHandler(IAppDbContext db, ICurrentUser current) : IQueryHandler<GetUserLlmSettingsQuery, GetUserLlmSettingsResult>
{
    public async Task<GetUserLlmSettingsResult> Handle(GetUserLlmSettingsQuery query, CancellationToken ct = default)
    {
        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;

        var owner = scope.AsT0;
        var settings = await db.UserLlmSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == owner, ct);
        return ToDto(settings);
    }

    public static UserLlmSettingsDto ToDto(UserLlmSettings? settings) =>
        new(LlmTasks.All.ToDictionary(t => t, t => settings?.ConnectionFor(t)));
}
