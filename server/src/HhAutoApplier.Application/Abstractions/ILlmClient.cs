namespace HhAutoApplier.Application.Abstractions;

// Порт к языковой модели: оценка вакансий, письма, ответы на анкеты. Реализация — в Infrastructure (OpenAI-совместимое API)
public interface ILlmClient
{
    // Ключ и модель заданы; без них вызывать бессмысленно
    bool IsConfigured { get; }

    // LlmUnavailableException — не настроен, исчерпан дневной лимит, провайдер не ответил после повторов или в ответе нет ни
    // одного варианта. Вариант без текста — не сбой: LlmResult с пустым Content, вызывающий считает его неразобранным (#81)
    Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct);
}

public record LlmMessage(string Role, string Content)
{
    public static LlmMessage System(string content) => new("system", content);

    public static LlmMessage User(string content) => new("user", content);

    public static LlmMessage Assistant(string content) => new("assistant", content);
}

// Json — попросить модель ответить JSON-объектом (response_format). Purpose — назначение запроса: по нему выбирается маршрут (#104)
public record LlmRequest(IReadOnlyList<LlmMessage> Messages, double Temperature = 0.2, int MaxTokens = 800, bool Json = false, string? Purpose = null);

// Модель — та, что ответила (провайдер может подставить версию), токены — для учёта расходов
public record LlmResult(string Content, string Model, int PromptTokens, int CompletionTokens);

// NotSent — запрос не ушёл к провайдеру (нет ключа, пауза, дневной лимит): конвейер не пишет такую попытку в журнал
// LlmCalls — во время паузы это были бы строки с полным промптом каждую минуту (#113)
public class LlmUnavailableException(string reason, bool notSent = false) : Exception($"LLM недоступна: {reason}")
{
    public bool NotSent { get; } = notSent;
}
