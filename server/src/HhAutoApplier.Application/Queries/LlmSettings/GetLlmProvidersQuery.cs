using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Llm;

namespace HhAutoApplier.Application.Queries.LlmSettings;

// Подключения LLM сервера и маршруты по умолчанию (#107) — любому вошедшему: подключения доступны всем (решение владельца)
public record GetLlmProvidersQuery;

public class GetLlmProvidersQueryHandler(ILlmConnections connections) : IQueryHandler<GetLlmProvidersQuery, LlmProvidersDto>
{
    public Task<LlmProvidersDto> Handle(GetLlmProvidersQuery query, CancellationToken ct = default) =>
        Task.FromResult(new LlmProvidersDto(
            connections.Named().Select(ToDto).ToList(),
            LlmTasks.All.Select(t => new LlmTaskDefaultDto(t, ToDto(connections.DefaultFor(t)))).ToList()));

    private static LlmConnectionDto ToDto(LlmConnectionStatus s) =>
        new(s.Name, s.Provider, s.Model, s.Available, s.PausedUntil, s.PauseReason, s.UsedToday, s.DailyLimit);
}
