namespace HhAutoApplier.Domain.Links;

// Агрегат: сбой связи (#132) — с начала (первая проверка с отказом) до конца (первая проверка без него). Журнал для разбора:
// что отказало, почему, сколько длилось. Причина и подробности — по последней проверке с отказом
public class LinkOutage
{
    // Для EF
    private LinkOutage()
    {
    }

    public long Id { get; private set; }

    public string Subject { get; private set; } = "";

    public OutageScope Scope { get; private set; }

    public LinkState Reason { get; private set; }

    public string? Detail { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    // Последняя проверка, видевшая сбой
    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public bool Open => EndedAt is null;

    public static LinkOutage Start(LinkFailure failure, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(failure.Subject))
            throw new DomainException("У сбоя связи нет направления");

        if (failure.Reason == LinkState.Ok)
            throw new DomainException("Сбой связи без причины");

        return new LinkOutage
        {
            Subject = failure.Subject,
            Scope = failure.Scope,
            Reason = failure.Reason,
            Detail = Cut(failure.Detail),
            StartedAt = at,
            LastSeenAt = at,
        };
    }

    // Сбой продолжается: причина могла смениться (таймаут → Bad Gateway)
    public void Continue(LinkFailure failure, DateTimeOffset at)
    {
        if (!Open)
            throw new DomainException("Сбой связи уже закончился");

        if (failure.Subject != Subject)
            throw new DomainException("Проверка другого направления");

        if (failure.Reason == LinkState.Ok)
            throw new DomainException("Сбой связи без причины");

        (Scope, Reason, Detail, LastSeenAt) = (failure.Scope, failure.Reason, Cut(failure.Detail), at > LastSeenAt ? at : LastSeenAt);
    }

    public void End(DateTimeOffset at)
    {
        if (!Open)
            throw new DomainException("Сбой связи уже закончился");

        EndedAt = at < LastSeenAt ? LastSeenAt : at;
    }

    public const int MaxDetailLength = 500;

    private static string? Cut(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? null : detail.Length > MaxDetailLength ? detail[..MaxDetailLength] : detail;
}
