namespace HhAutoApplier.Application.Abstractions;

// Состояние подключений LLM сервера (#107) для выбора пользователем: без ключей и адресов
public interface ILlmConnections
{
    // Именованные подключения Llm:Providers:<имя>
    IReadOnlyList<LlmConnectionStatus> Named();

    // Чем сервер выполняет задачу без выбора пользователя: маршрут задачи (#104) или основной провайдер
    LlmConnectionStatus DefaultFor(string task);

    bool Exists(string name);
}

// PausedUntil — пауза после 429 или сбоя сети; DailyLimit 0 — без ограничения
public record LlmConnectionStatus(string Name, string Provider, string Model, bool Configured, DateTimeOffset? PausedUntil, string? PauseReason,
    int UsedToday, int DailyLimit)
{
    public bool Available => Configured && PausedUntil is null && (DailyLimit == 0 || UsedToday < DailyLimit);
}
