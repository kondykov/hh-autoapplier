using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Infrastructure.Llm;

// Клиент chat/completions для OpenAI-совместимых провайдеров (Groq, OpenAI, OpenRouter, локальные серверы). Различия провайдеров — в LlmProviderProfile
public class OpenAiCompatibleClient : ILlmClient
{
    private const string NoText = "в ответе провайдера нет текста";

    private readonly HttpClient _http;

    private readonly LlmOptions _opt;

    private readonly LlmProviderProfile _profile;

    private readonly LlmBudget _budget;

    private readonly ILogger<OpenAiCompatibleClient> _logger;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // null — постоянный ключ в заголовке Bearer, иначе токен доступа на каждый запрос
    private readonly ILlmTokenSource? _tokens;

    public OpenAiCompatibleClient(
        HttpClient http,
        IOptions<LlmOptions> options,
        LlmBudget budget,
        ILogger<OpenAiCompatibleClient> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILlmTokenSource? tokens = null)
    {
        _opt = options.Value;
        _profile = _opt.GetProfile();
        _http = http;
        _budget = budget;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        _tokens = tokens;
        _http.BaseAddress = new Uri(_opt.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(_opt.TimeoutSeconds);
        if (IsConfigured && _tokens is null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey) && !string.IsNullOrWhiteSpace(_opt.Model);

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new LlmUnavailableException("не задан ключ API (LLM_API_KEY) или модель");
        if (_budget.Throttle is { } throttle)
            throw new LlmUnavailableException(Throttled(throttle.Reason, throttle.Until));
        if (!_budget.TryTake(_opt.DailyRequestLimit))
            throw new LlmUnavailableException($"исчерпан дневной лимит запросов ({_opt.DailyRequestLimit})");

        var body = new ChatRequest(
            _opt.Model,
            request.Messages.Select(m => new ChatMessage(m.Role, m.Content)).ToList(),
            _profile.Temperature(request.Temperature),
            request.MaxTokens,
            // Без json_object JSON задаётся промптом: разбор и так берёт объект из текста ответа
            request.Json && _opt.UsesJsonObject() ? new ResponseFormat("json_object") : null);

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(body, ct);
            }
            catch (HttpRequestException e) when (attempt < _opt.MaxRetries)
            {
                await PauseAsync(attempt, null, $"сеть: {e.Message}", ct);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmUnavailableException($"нет ответа за {_opt.TimeoutSeconds} с");
            }
            catch (HttpRequestException e)
            {
                // Запрос до провайдера не дошёл (#68): лимит возвращается, четыре конвейера не долбят мёртвую сеть каждую минуту
                var reason = $"сеть: {e.Message}";
                var until = _budget.ThrottleFor(NetworkPause, reason);
                _budget.Return();
                throw new LlmUnavailableException(Throttled(reason, until));
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                    return await ReadAsync(response, ct);

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                var error = await response.Content.ReadAsStringAsync(ct);
                if (_tokens is not null && response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new LlmUnavailableException("провайдер отклонил токен доступа (401) и после его обновления");

                if (!retryable || attempt >= _opt.MaxRetries)
                    throw new LlmUnavailableException($"провайдер ответил {(int)response.StatusCode}: {Trim(error)}");

                var retryAfter = RetryAfter(response);
                // Долгий срок провайдера не ждём внутри прохода (#85): он держал бы оценку, письма и анкеты до 20 минут.
                // Проход останавливается, до срока запросы не идут, единица лимита возвращается — модель запрос не обработала
                if (response.StatusCode == HttpStatusCode.TooManyRequests && retryAfter > TimeSpan.FromSeconds(_opt.MaxRetryAfterSeconds))
                {
                    var limitedUntil = _budget.ThrottleFor(retryAfter.Value, RateLimited);
                    _budget.Return();
                    _logger.LogWarning("LLM: ответ 429, Retry-After {Pause} с — проход остановлен до {Until:HH:mm:ss} UTC", retryAfter.Value.TotalSeconds, limitedUntil);
                    throw new LlmUnavailableException(Throttled(RateLimited, limitedUntil));
                }

                await PauseAsync(attempt, retryAfter, $"ответ {(int)response.StatusCode}", ct);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(ChatRequest body, CancellationToken ct)
    {
        if (_tokens is null)
            return await _http.PostAsJsonAsync("chat/completions", body, ct);

        var token = await _tokens.GetAsync(ct);
        var response = await PostWithTokenAsync(body, token, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // Токен отозван или истёк раньше срока: один новый обмен, второй 401 уходит вызывающему
        response.Dispose();
        _tokens.Invalidate(token);
        return await PostWithTokenAsync(body, await _tokens.GetAsync(ct), ct);
    }

    private async Task<HttpResponseMessage> PostWithTokenAsync(ChatRequest body, string token, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(message, ct);
    }

    private async Task<LlmResult> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ChatResponse? chat;
        try
        {
            chat = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        }
        catch (JsonException)
        {
            throw new LlmUnavailableException(NoText);
        }

        if (chat?.Choices?.FirstOrDefault() is not { } choice)
            throw new LlmUnavailableException(NoText);

        // Вариант есть, текста нет (модель потратила токены на рассуждения): это ответ на этот запрос, а не сбой провайдера (#81).
        // Пустой текст идёт путём неразобранного ответа. Модель не пустая: без неё запись в журнале считалась бы сбоем провайдера
        var model = string.IsNullOrEmpty(chat.Model) ? _opt.Model : chat.Model;
        return new LlmResult(choice.Message?.Content ?? "", model, chat.Usage?.PromptTokens ?? 0, chat.Usage?.CompletionTokens ?? 0);
    }

    private Task PauseAsync(int attempt, TimeSpan? retryAfter, string reason, CancellationToken ct)
    {
        var pause = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        _logger.LogWarning("LLM: {Reason}, повтор {Attempt}/{Max} через {Pause} с", reason, attempt + 1, _opt.MaxRetries, pause.TotalSeconds);
        return _delay(pause, ct);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;

    private const string RateLimited = "провайдер ограничил частоту запросов";

    // После сетевой ошибки (уже с повторами) — пауза перед следующей попыткой любого конвейера
    private static readonly TimeSpan NetworkPause = TimeSpan.FromMinutes(5);

    // Пауза у своего маршрута (модели), остальные работают; время — московское (#108)
    private string Throttled(string reason, DateTimeOffset until) => $"{reason}; запросы к {_opt.Model} приостановлены до {MoscowTime.Format(until)}";

    // Retry-After — секунды или дата
    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } header
            ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
            : null;

    private record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResponseFormat? ResponseFormat);

    private record ChatMessage([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("content")] string? Content);

    private record ResponseFormat([property: JsonPropertyName("type")] string Type);

    private record ChatResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("choices")] List<ChatChoice>? Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    private record ChatChoice([property: JsonPropertyName("message")] ChatMessage? Message);

    private record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}
