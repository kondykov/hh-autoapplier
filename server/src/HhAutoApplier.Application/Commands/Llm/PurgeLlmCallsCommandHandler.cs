using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Llm;

// Сколько записей удалено
public class PurgeLlmCallsCommandHandler(IAppDbContext db, TimeProvider clock) : ICommandHandler<PurgeLlmCallsCommand, int>
{
    public Task<int> Handle(PurgeLlmCallsCommand command, CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - PurgeLlmCallsCommand.Retention;
        return db.LlmCalls.Where(c => c.CreatedAt < before).ExecuteDeleteAsync(ct);
    }
}
