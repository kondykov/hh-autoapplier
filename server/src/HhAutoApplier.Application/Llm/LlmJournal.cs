using HhAutoApplier.Domain.Vacancies;

namespace HhAutoApplier.Application.Llm;

// Журнал вызовов LLM для вкладки «LLM → Журнал» (#127): одинаковые ошибки подряд сворачиваются в одну строку
// («лимит исчерпан ×1968» с диапазоном времени) — на копии прода из ~4,4 тыс. вызовов ~3 тыс. таких повторов
public static partial class LlmJournal
{
    public const string Ok = "ok";

    // Модель ответила, но ответ не принят: не разобран или не прошёл проверку (письмо, анкета)
    public const string Rejected = "rejected";

    // Ответа не было: лимит, сеть, провайдер
    public const string Error = "error";

    // Строка журнала без промпта и ответа: их читают по одной (GET /api/llm/calls/{id})
    public record Row(long Id, string Purpose, string Model, string? Connection, string? Error, bool HasResponse, int PromptTokens, int CompletionTokens,
        Guid? UserId, string? VacancyId, DateTimeOffset At);

    // Свёрнутая строка: последний вызов группы (Id, At), первый — Since; Count — сколько вызовов подряд
    public record Entry(Row Last, DateTimeOffset Since, int Count, IReadOnlyList<string> Purposes)
    {
        public string Outcome => OutcomeOf(Last);
    }

    public static string OutcomeOf(Row row) => row.Error is null ? Ok : row.HasResponse ? Rejected : Error;

    // rows — от новых к старым. Сворачиваются ошибки без ответа внутри одной серии ошибок (её прерывает любой ответ модели):
    // одинаковые по тексту без цифр («пауза до 14:44» и «до 14:49» — одно), модели и пользователю. Ошибки разных задач
    // идут вперемешку («сеть» у писем, «токен» у оценки) — каждая сворачивается в свою строку.
    // Отклонённые проверкой ответы и удачные вызовы — каждый своей строкой
    public static List<Entry> Collapse(IEnumerable<Row> rows)
    {
        var entries = new List<Entry>();
        var run = new Dictionary<(string, string, Guid?), int>();
        foreach (var row in rows)
        {
            if (OutcomeOf(row) != Error)
            {
                run.Clear();
                entries.Add(new Entry(row, row.At, 1, [row.Purpose]));
                continue;
            }

            var key = (Signature(row.Error!), row.Model, row.UserId);
            if (run.TryGetValue(key, out var at))
            {
                var e = entries[at];
                var purposes = e.Purposes.Contains(row.Purpose) ? e.Purposes : [.. e.Purposes, row.Purpose];
                entries[at] = e with { Since = row.At, Count = e.Count + 1, Purposes = purposes };
                continue;
            }

            run[key] = entries.Count;
            entries.Add(new Entry(row, row.At, 1, [row.Purpose]));
        }

        return entries;
    }

    // Текст ошибки без чисел: время паузы, порты и счётчики не делают ошибку другой
    public static string Signature(string error) => Digits().Replace(error, "#");

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+")]
    private static partial System.Text.RegularExpressions.Regex Digits();

    // Итог удачной оценки одной строкой: «подходит 82», «сомнительно 55», «не подходит 30»; остальные задачи — без итога
    public static string? Verdict(string purpose, string? response)
    {
        if (purpose != VacancyAssessment.Purpose || response is null || VacancyAssessment.Parse(response, []) is not { } a)
            return null;

        var verdict = a.Verdict switch
        {
            LlmVerdict.Fit => "подходит",
            LlmVerdict.Doubtful => "сомнительно",
            _ => "не подходит",
        };
        return $"{verdict} {a.Score}";
    }
}
