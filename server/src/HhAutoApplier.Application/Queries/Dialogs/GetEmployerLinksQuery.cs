using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Dialogs;
using Microsoft.EntityFrameworkCore;
using OneOf;

namespace HhAutoApplier.Application.Queries.Dialogs;

public record GetEmployerLinksQuery(Guid? UserId = null);

[GenerateOneOf]
public partial class GetEmployerLinksResult : OneOfBase<List<EmployerLinkDto>, ForbiddenError>;

public class GetEmployerLinksQueryHandler(IAppDbContext db, ICurrentUser current)
    : IQueryHandler<GetEmployerLinksQuery, GetEmployerLinksResult>
{
    public async Task<GetEmployerLinksResult> Handle(GetEmployerLinksQuery query, CancellationToken ct = default)
    {
        var scope = OwnerScope.Resolve(current, query.UserId);
        if (scope.IsT1)
            return scope.AsT1;
        var messages = await db.EmployerMessages.AsNoTracking()
            .Where(message => message.UserId == scope.AsT0 && message.ExternalLinks.Any()).ToListAsync(ct);
        return messages.SelectMany(message => message.ExternalLinks.Where(link => link.RequiresAction != false)
                .Select(link => EmployerLinkDto.From(message, link)))
            .OrderBy(link => link.State != EmployerLinkState.Pending).ThenByDescending(link => link.DetectedAt).ToList();
    }
}
