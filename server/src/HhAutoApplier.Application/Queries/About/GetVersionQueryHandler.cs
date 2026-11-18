using System.Reflection;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Application.Queries.About;

public class GetVersionQueryHandler(IOptions<StageOptions> stage) : IQueryHandler<GetVersionQuery, BuildVersionDto>
{
    private const int ShortCommitLength = 7;

    public Task<BuildVersionDto> Handle(GetVersionQuery query, CancellationToken ct = default)
    {
        // Сборка записывает «0.1.0+<коммит>»: коммит берётся из git, в образе Docker — из аргумента GIT_COMMIT
        var informational = typeof(ApiContract).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = informational.Split('+', 2);
        var commit = parts.Length > 1 ? parts[1][..Math.Min(ShortCommitLength, parts[1].Length)] : "unknown";

        var label = stage.Value.Enabled ? stage.Value.Label!.Trim() : null;
        return Task.FromResult(new BuildVersionDto(parts[0], commit, ApiContract.Version, label));
    }
}
