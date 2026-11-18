using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Links;

namespace HhAutoApplier.Application.Extractors;

public class LinkOutageExtractor : IExtractor<LinkOutage, LinkOutageDto>
{
    public LinkOutageDto Extract(LinkOutage source) =>
        new(source.Id, source.Subject, source.Scope, source.Reason, source.Detail, source.StartedAt, source.LastSeenAt, source.EndedAt);
}
