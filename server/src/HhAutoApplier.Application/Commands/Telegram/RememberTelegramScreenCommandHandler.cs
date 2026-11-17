using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Telegram;

public class RememberTelegramScreenCommandHandler(IAppDbContext db) : ICommandHandler<RememberTelegramScreenCommand, bool>
{
    public async Task<bool> Handle(RememberTelegramScreenCommand command, CancellationToken ct = default)
    {
        var chat = await db.TelegramChats.FirstOrDefaultAsync(c => c.ChatId == command.ChatId, ct);
        if (chat is null || command.MessageId <= 0)
            return false;

        chat.ShowScreen(command.MessageId);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
