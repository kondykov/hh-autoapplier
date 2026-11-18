using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Links;
using HhAutoApplier.Domain.Links;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Links;

// Сбой открывается на первой проверке с отказом и закрывается на первой без него. Направление, которое больше не проверяется
// (провайдер убран из настроек), закрывается: иначе сбой висел бы открытым вечно
public class RecordLinkCheckCommandHandler(IAppDbContext db, LinkStatus status, TimeProvider clock)
    : ICommandHandler<RecordLinkCheckCommand, LinkCheckResult>
{
    public async Task<LinkCheckResult> Handle(RecordLinkCheckCommand command, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var probes = command.Probes;
        status.Observe(probes, now);

        var verdict = LinkClassifier.Classify(probes);
        var checkable = probes.Select(p => p.Link).ToHashSet();
        if (probes.Any(p => p.ViaProxy))
            checkable.Add(LinkClassifier.VpnNode);

        var open = await db.LinkOutages.Where(o => o.EndedAt == null).ToListAsync(ct);
        var started = new List<LinkFailure>();
        foreach (var failure in verdict.Failures)
        {
            if (open.FirstOrDefault(o => o.Subject == failure.Subject) is { } outage)
                outage.Continue(failure, now);
            else
            {
                db.LinkOutages.Add(LinkOutage.Start(failure, now));
                started.Add(failure);
            }
        }

        var ended = new List<string>();
        foreach (var outage in open.Where(o => verdict.Failures.All(f => f.Subject != o.Subject)
                                               && (verdict.Judged.Contains(o.Subject) || !checkable.Contains(o.Subject))))
        {
            outage.End(now);
            ended.Add(outage.Subject);
        }

        await db.SaveChangesAsync(ct);
        var telegramOk = probes.Any(p => p.Link == LinkProbe.Telegram && p.Ok);
        return new LinkCheckResult(verdict, started, ended, telegramOk);
    }
}
