namespace HhAutoApplier.Application.Dtos;

// Подключения LLM сервера для выбора пользователем (#107): без ключей и адресов
public record LlmProvidersDto(IReadOnlyList<LlmConnectionDto> Providers, IReadOnlyList<LlmTaskDefaultDto> Defaults);

// Available — ключ задан, нет паузы и лимит не исчерпан; DailyLimit 0 — без ограничения
public record LlmConnectionDto(string Name, string Provider, string Model, bool Available, DateTimeOffset? PausedUntil, string? PauseReason,
    int UsedToday, int DailyLimit);

// Чем сервер выполняет задачу без выбора пользователя
public record LlmTaskDefaultDto(string Task, LlmConnectionDto Connection);

// Выбор пользователя: задача → имя подключения; null — как на сервере
public record UserLlmSettingsDto(IReadOnlyDictionary<string, string?> Choices);
