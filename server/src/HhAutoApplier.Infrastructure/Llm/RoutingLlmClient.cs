using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Llm;

namespace HhAutoApplier.Infrastructure.Llm;

// Выбор LLM под задачу (#104): назначение запроса → маршрут из Llm:Routes, иначе основной провайдер.
// У каждого маршрута свой клиент со своим счётчиком и паузой: 429 у одного провайдера не останавливает другие
public sealed class RoutingLlmClient(ILlmClient main, IReadOnlyDictionary<string, ILlmClient> routes) : ILlmClient
{
    public bool IsConfigured => main.IsConfigured || routes.Values.Any(r => r.IsConfigured);

    public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct) => For(request.Purpose).CompleteAsync(request, ct);

    public ILlmClient For(string? purpose) =>
        LlmRoutes.Of(purpose) is { } route && routes.TryGetValue(route, out var client) && client.IsConfigured ? client : main;
}
