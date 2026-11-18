using HhAutoApplier.Domain.Links;

namespace HhAutoApplier.Application.Links;

// Направление по последней проверке: LastOkAt — последний успех за время работы приложения
public sealed record LinkSnapshot(string Link, bool ViaProxy, LinkState State, string? Detail, DateTimeOffset CheckedAt, DateTimeOffset? LastOkAt);

// Текущее состояние связи (#132): в памяти, обновляется каждой проверкой; журнал сбоев — в базе (LinkOutage)
public class LinkStatus
{
    private readonly object _lock = new();

    private readonly Dictionary<string, LinkSnapshot> _links = [];

    // Проверка включена; Proxy — адрес прокси без учётных данных («host:port»), null — без прокси
    public bool Enabled { get; private set; }

    public string? Proxy { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    public void Configure(bool enabled, string? proxy)
    {
        lock (_lock)
            (Enabled, Proxy) = (enabled, proxy);
    }

    public void Observe(IReadOnlyList<LinkProbe> probes, DateTimeOffset at)
    {
        lock (_lock)
        {
            CheckedAt = at;
            foreach (var probe in probes)
            {
                var lastOk = probe.Ok ? at : _links.GetValueOrDefault(probe.Link)?.LastOkAt;
                _links[probe.Link] = new LinkSnapshot(probe.Link, probe.ViaProxy, probe.State, probe.Detail, at, lastOk);
            }
        }
    }

    public IReadOnlyList<LinkSnapshot> Snapshot()
    {
        lock (_lock)
            return [.. _links.Values.OrderBy(l => l.Link == LinkProbe.Proxy ? 0 : 1).ThenBy(l => l.Link, StringComparer.Ordinal)];
    }
}
