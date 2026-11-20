using HhAutoApplier.Application.Abstractions;

namespace HhAutoApplier.Infrastructure.Llm;

// Настройки и счётчик одного клиента LLM: основного провайдера, маршрута задачи или именованного подключения
public sealed record LlmEndpoint(string Name, LlmOptions Options, LlmBudget Budget)
{
    public LlmConnectionStatus Status()
    {
        var throttle = Budget.Throttle;
        var configured = !string.IsNullOrWhiteSpace(Options.ApiKey) && !string.IsNullOrWhiteSpace(Options.Model);
        return new LlmConnectionStatus(Name, Options.GetProfile().Name, Options.Model, configured, throttle?.Until, throttle?.Reason,
            Budget.Used, Options.DailyRequestLimit);
    }
}

// Подключения сервера для выбора пользователем (#107): состояние берётся у тех же счётчиков, что у клиентов, — пауза и расход совпадают
public sealed class LlmConnectionRegistry(
    LlmEndpoint main,
    IReadOnlyDictionary<string, LlmEndpoint> routes,
    IReadOnlyList<LlmEndpoint> named
) : ILlmConnections
{
    public const string MainName = "main";

    public IReadOnlyList<LlmConnectionStatus> Named() => named.Select(e => e.Status()).ToList();

    // Как RoutingLlmClient: маршрут задачи с ключом, иначе основной провайдер
    public LlmConnectionStatus DefaultFor(string task) =>
        routes.TryGetValue(task, out var route) && route.Status() is { Configured: true } status ? status : main.Status();

    public bool Exists(string name) => named.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
}
