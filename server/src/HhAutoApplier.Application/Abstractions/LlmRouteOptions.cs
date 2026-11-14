namespace HhAutoApplier.Application.Abstractions;

// Маршрут LLM под задачу (#104): Llm:Routes:<имя>. Незаданное берётся из основных Llm:*; маршрут без провайдера, адреса,
// ключа и модели не действует — его запросы идут основным провайдером
public class LlmRouteOptions
{
    public string? Provider { get; set; }

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    public string? JsonMode { get; set; }

    public string? Scope { get; set; }

    public string? AuthUrl { get; set; }

    // Свой дневной лимит провайдера маршрута; пусто — как Llm:DailyRequestLimit
    public int? DailyRequestLimit { get; set; }

    public bool IsSet => !string.IsNullOrWhiteSpace(Provider) || !string.IsNullOrWhiteSpace(BaseUrl)
                         || !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(Model);

    // Другой провайдер или адрес — свой ключ: ключ основного провайдера не должен уйти чужому
    public bool ChangesProvider => !string.IsNullOrWhiteSpace(Provider) || !string.IsNullOrWhiteSpace(BaseUrl);

    public LlmOptions Over(LlmOptions main) => new()
    {
        Provider = Or(Provider, main.Provider),
        BaseUrl = Or(BaseUrl, main.BaseUrl),
        ApiKey = ChangesProvider ? Blank(ApiKey) : Or(ApiKey, main.ApiKey ?? ""),
        Model = Or(Model, main.Model),
        JsonMode = Blank(JsonMode) ?? (ChangesProvider ? null : main.JsonMode),
        Scope = Or(Scope, main.Scope),
        AuthUrl = Or(AuthUrl, main.AuthUrl),
        TimeoutSeconds = main.TimeoutSeconds,
        MaxRetries = main.MaxRetries,
        MaxRetryAfterSeconds = main.MaxRetryAfterSeconds,
        DailyRequestLimit = DailyRequestLimit ?? main.DailyRequestLimit,
        UserDailyRequestLimit = main.UserDailyRequestLimit,
    };

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
