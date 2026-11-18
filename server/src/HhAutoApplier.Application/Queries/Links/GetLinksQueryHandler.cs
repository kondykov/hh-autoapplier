using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Links;
using HhAutoApplier.Domain.Links;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Queries.Links;

public class GetLinksQueryHandler(IAppDbContext db, LinkStatus status, IExtractor<LinkOutage, LinkOutageDto> outages)
    : IQueryHandler<GetLinksQuery, LinksDto>
{
    // Закончившихся сбоев в ответе — последние столько
    private const int RecentCount = 20;

    public async Task<LinksDto> Handle(GetLinksQuery query, CancellationToken ct = default)
    {
        var open = await db.LinkOutages.AsNoTracking().Where(o => o.EndedAt == null).OrderBy(o => o.StartedAt).ToListAsync(ct);
        var recent = await db.LinkOutages.AsNoTracking().Where(o => o.EndedAt != null)
            .OrderByDescending(o => o.EndedAt).Take(RecentCount).ToListAsync(ct);
        var waiting = await db.Notifications.CountAsync(n => n.SentAt == null && n.WaitingSince != null, ct);

        return new LinksDto(
            status.Enabled,
            status.Proxy,
            status.CheckedAt,
            [.. status.Snapshot().Select(l => new LinkStateDto(l.Link, l.ViaProxy, l.State, l.Detail, l.CheckedAt, l.LastOkAt))],
            [.. open.Select(outages.Extract)],
            [.. recent.Select(outages.Extract)],
            waiting);
    }
}
