using HhAutoApplier.Application.Abstractions;

namespace HhAutoApplier.Application.Llm;

// Какой маршрут LLM у назначения запроса (#104). Назначение не из таблицы (проверка связи) — основной провайдер
public static class LlmRoutes
{
    public const string Evaluation = "Evaluation";

    public const string Letters = "Letters";

    public const string Questionnaires = "Questionnaires";

    public const string Dialogs = "Dialogs";

    public static readonly IReadOnlyList<string> All = [Evaluation, Letters, Questionnaires, Dialogs];

    public static readonly IReadOnlyDictionary<string, string> ByPurpose = new Dictionary<string, string>
    {
        [VacancyAssessment.Purpose] = Evaluation,
        // Разбор просьб к письму — тот же разбор текста вакансии, что и оценка
        [LetterRequestExtraction.Purpose] = Evaluation,
        [CoverLetterWriting.Purpose] = Letters,
        [QuestionnaireAnswering.Purpose] = Questionnaires,
        [DialogAnalysis.Purpose] = Dialogs,
    };

    public static string? Of(string? purpose) => purpose is not null && ByPurpose.TryGetValue(purpose, out var route) ? route : null;

    public static IEnumerable<string> PurposesOf(string route) => ByPurpose.Where(p => p.Value == route).Select(p => p.Key);

    // Действующие маршруты с итоговыми настройками; ошибка конфигурации — при старте, а не на первом запросе
    public static IReadOnlyDictionary<string, LlmOptions> Resolve(LlmOptions main)
    {
        var resolved = new Dictionary<string, LlmOptions>();
        foreach (var (name, route) in main.Routes)
        {
            var known = All.FirstOrDefault(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Llm:Routes: неизвестный маршрут «{name}», допустимы {string.Join(", ", All)}");
            if (!route.IsSet)
                continue;
            if (route.ChangesProvider && string.IsNullOrWhiteSpace(route.ApiKey))
                throw new InvalidOperationException($"Llm:Routes:{known}: другой провайдер или адрес — задайте и свой ключ (ApiKey): ключ основного провайдера ему не передаётся");

            var options = route.Over(main);
            try
            {
                options.EnsureValid();
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Llm:Routes:{known}: {e.Message}", e);
            }

            resolved[known] = options;
        }

        return resolved;
    }
}
