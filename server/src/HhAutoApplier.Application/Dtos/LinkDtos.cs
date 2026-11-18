using HhAutoApplier.Domain.Links;

namespace HhAutoApplier.Application.Dtos;

// Состояние связи (#132): направления по последней проверке, открытые и последние сбои, сколько уведомлений ждёт связи
public record LinksDto(
    bool Enabled,
    string? Proxy,
    DateTimeOffset? CheckedAt,
    IReadOnlyList<LinkStateDto> Links,
    IReadOnlyList<LinkOutageDto> Open,
    IReadOnlyList<LinkOutageDto> Recent,
    int WaitingNotifications);

public record LinkStateDto(string Link, bool ViaProxy, LinkState State, string? Detail, DateTimeOffset CheckedAt, DateTimeOffset? LastOkAt);

public record LinkOutageDto(
    long Id,
    string Subject,
    OutageScope Scope,
    LinkState Reason,
    string? Detail,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? EndedAt);
