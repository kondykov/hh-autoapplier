using HhAutoApplier.Domain.Vacancies;

namespace HhAutoApplier.Application.Llm;

// Журнал вызовов LLM для вкладки «LLM → Журнал» (#127): одинаковые ошибки подряд сворачиваются в одну строку
// («лимит исчерпан ×1968» с диапазоном времени) — на копии прода из ~4,4 тыс. вызовов ~3 тыс. таких повторов
public static class LlmJournal
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

    // rows — от новых к старым. Сворачиваются только ошибки без ответа с одинаковым текстом и моделью, идущие подряд;
    // отклонённые проверкой ответы и удачные вызовы — каждый своей строкой
    public static List<Entry> Collapse(IEnumerable<Row> rows)
    {
        var entries = new List<Entry>();
        foreach (var row in rows)
        {
            if (entries.Count > 0 && entries[^1] is var last && OutcomeOf(row) == Error && last.Outcome == Error
                && row.Error == last.Last.Error && row.Model == last.Last.Model && row.UserId == last.Last.UserId)
            {
                var purposes = last.Purposes.Contains(row.Purpose) ? last.Purposes : [.. last.Purposes, row.Purpose];
                entries[^1] = last with { Since = row.At, Count = last.Count + 1, Purposes = purposes };
                continue;
            }

            entries.Add(new Entry(row, row.At, 1, [row.Purpose]));
        }

        return entries;
    }

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
