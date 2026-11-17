using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Dialogs;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Queries.Dialogs;

public class GetDialogQueryHandler(
    IAppDbContext db,
    IExtractor<EmployerMessage, EmployerMessageDto> extractor,
    ICurrentUser current
) : IQueryHandler<GetDialogQuery, GetDialogResult>
{
    public async Task<GetDialogResult> Handle(GetDialogQuery query, CancellationToken ct = default)
    {
        var message = await db.EmployerMessages.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == current.Id && m.ChatId == query.ChatId, ct);
        return message is null ? new NotFoundError($"Чат {query.ChatId} не найден") : extractor.Extract(message);
    }
}
