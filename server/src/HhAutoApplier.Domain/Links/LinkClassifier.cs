namespace HhAutoApplier.Domain.Links;

// Что отказало: сам прокси, узел VPN (через прокси не ходит ничего) или одно направление (вероятно, сторона сервиса)
public enum OutageScope
{
    Proxy = 0,
    VpnNode = 1,
    Direction = 2,
}

// Сбой в одной проверке: Subject — что отказало (имя направления, Proxy или VpnNode)
public sealed record LinkFailure(string Subject, OutageScope Scope, LinkState Reason, string? Detail);

// Judged — о чём проверка дала ответ (у них сбой открывается или закрывается); Failures — что из этого отказало.
// Не оценённое (направления через прокси, когда лежит сам прокси) остаётся как было
public sealed record LinkVerdict(IReadOnlyList<string> Judged, IReadOnlyList<LinkFailure> Failures)
{
    public bool Failing => Failures.Count > 0;
}

// Разбор одной проверки (#132): лежит прокси — направления через него не судятся; отказали все направления через прокси
// (два и больше) — «узел VPN»; отказало не всё — сбой у каждого отказавшего направления
public static class LinkClassifier
{
    public const string VpnNode = "vpn";

    public static LinkVerdict Classify(IReadOnlyList<LinkProbe> probes)
    {
        var judged = new List<string>();
        var failures = new List<LinkFailure>();

        foreach (var probe in probes.Where(p => !p.ViaProxy && p.Link != LinkProbe.Proxy))
            Judge(probe.Link, probe, OutageScope.Direction);

        var proxy = probes.FirstOrDefault(p => p.Link == LinkProbe.Proxy);
        var via = probes.Where(p => p.ViaProxy).ToList();
        if (proxy is not null)
        {
            Judge(LinkProbe.Proxy, proxy, OutageScope.Proxy);
            if (!proxy.Ok)
                return new LinkVerdict(judged, failures);
        }

        if (via.Count == 0)
            return new LinkVerdict(judged, failures);

        judged.Add(VpnNode);
        if (via.Count >= 2 && via.All(p => !p.Ok))
        {
            var reasons = via.Select(p => p.State).Distinct().ToList();
            var detail = string.Join("; ", via.Select(p => $"{p.Link}: {p.Detail ?? p.State.ToString()}"));
            failures.Add(new LinkFailure(VpnNode, OutageScope.VpnNode, reasons.Count == 1 ? reasons[0] : LinkState.Error, detail));
            return new LinkVerdict(judged, failures);
        }

        foreach (var probe in via)
            Judge(probe.Link, probe, OutageScope.Direction);
        return new LinkVerdict(judged, failures);

        void Judge(string subject, LinkProbe probe, OutageScope scope)
        {
            judged.Add(subject);
            if (!probe.Ok)
                failures.Add(new LinkFailure(subject, scope, probe.State, probe.Detail));
        }
    }
}
