using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Dialogs;
using Microsoft.EntityFrameworkCore;
using OneOf;

namespace HhAutoApplier.Application.Commands.Dialogs;

public enum EmployerLinkDecision { Complete = 1, Dismiss, Remind }

public record DecideEmployerLinkCommand(long ChatId, string Key, EmployerLinkDecision Decision);

[GenerateOneOf]
public partial class DecideEmployerLinkResult : OneOfBase<EmployerLinkDto, NotFoundError, ConflictError>;

public class DecideEmployerLinkCommandHandler(IAppDbContext db, ICurrentUser current, TimeProvider clock)
    : ICommandHandler<DecideEmployerLinkCommand, DecideEmployerLinkResult>
{
    public async Task<DecideEmployerLinkResult> Handle(DecideEmployerLinkCommand command, CancellationToken ct = default)
    {
        var message = await db.EmployerMessages.SingleOrDefaultAsync(message => message.UserId == current.Id
            && message.ChatId == command.ChatId, ct);
        var link = message?.ExternalLinks.FirstOrDefault(link => link.Key == command.Key && link.RequiresAction != false);
        if (message is null || link is null)
            return new NotFoundError("Ссылка не найдена");

        if (link.State != EmployerLinkState.Pending)
            return EmployerLinkDto.From(message, link);
        switch (command.Decision)
        {
            case EmployerLinkDecision.Complete:
                link.Resolve(EmployerLinkState.Completed);
                break;
            case EmployerLinkDecision.Dismiss:
                link.Resolve(EmployerLinkState.Dismissed);
                break;
            case EmployerLinkDecision.Remind:
                if (link.RemindedAt.HasValue)
                    return new ConflictError("Напоминание уже отправлено");
                link.RemindTomorrow(clock.GetUtcNow());
                break;
            default:
                return new ConflictError("Неизвестное действие со ссылкой");
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ConflictError("Чат обновился, откройте список ссылок заново");
        }
        return EmployerLinkDto.From(message, link);
    }
}
