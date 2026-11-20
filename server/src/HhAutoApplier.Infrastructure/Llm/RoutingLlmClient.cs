using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HhAutoApplier.Infrastructure.Llm;

// Выбор LLM под запрос: подключение, выбранное владельцем отклика (#107), иначе маршрут задачи из Llm:Routes (#104), иначе основной провайдер.
// У каждого подключения и маршрута свой клиент со своим счётчиком и паузой: 429 у одного провайдера не останавливает другие
public sealed class RoutingLlmClient(
    ILlmClient main,
    IReadOnlyDictionary<string, ILlmClient> routes,
    IReadOnlyDictionary<string, ILlmClient>? connections = null,
    ILogger<RoutingLlmClient>? logger = null
) : ILlmClient
{
    private readonly IReadOnlyDictionary<string, ILlmClient> _connections = connections ?? new Dictionary<string, ILlmClient>();

    private readonly ILogger _logger = logger ?? NullLogger<RoutingLlmClient>.Instance;

    public bool IsConfigured => main.IsConfigured || routes.Values.Any(r => r.IsConfigured) || _connections.Values.Any(c => c.IsConfigured);

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (request.Provider is { } name)
        {
            // Имя без учёта регистра; в журнал — как в конфигурации
            var (connection, client) = _connections.FirstOrDefault(c => string.Equals(c.Key, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (client is { IsConfigured: true })
                return await client.CompleteAsync(request, ct) with { Connection = connection };

            // Подключение убрали из конфигурации после выбора пользователя: задача не встаёт, идёт маршрутом по умолчанию
            _logger.LogWarning("LLM: подключения «{Connection}» нет на сервере или у него нет ключа — {Purpose} идёт маршрутом по умолчанию",
                name, request.Purpose);
        }

        return await For(request.Purpose).CompleteAsync(request, ct);
    }

    public ILlmClient For(string? purpose) =>
        LlmRoutes.Of(purpose) is { } route && routes.TryGetValue(route, out var client) && client.IsConfigured ? client : main;
}
