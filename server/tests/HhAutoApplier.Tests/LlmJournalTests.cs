using HhAutoApplier.Application.Llm;
using HhAutoApplier.Application.Queries.Llm;

namespace HhAutoApplier.Tests;

// Журнал LLM (#127): свёртка повторов одной ошибки подряд, итог оценки, маска ключа и адрес без пути для «Подключений»
public class LlmJournalTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static LlmJournal.Row Row(long id, string? error, bool response = false, string purpose = "vacancy-evaluation", string model = "", Guid? user = null) =>
        new(id, purpose, model, null, error, response, 0, 0, user, null, T0.AddMinutes(-id));

    [Fact]
    public void Collapse_RepeatsOfSameErrorInARow()
    {
        const string limit = "LLM недоступна: исчерпан дневной лимит (300)";
        var rows = new[]
        {
            Row(1, limit), Row(2, limit, purpose: "cover-letter"), Row(3, limit),
            Row(4, null, response: true, model: "m"),
            Row(5, limit), Row(6, "LLM недоступна: SSL"),
            Row(7, "цифры не из фактов: 15", response: true, model: "m"), Row(8, "цифры не из фактов: 15", response: true, model: "m"),
        };

        var entries = LlmJournal.Collapse(rows);

        Assert.Equal([3, 1, 1, 1, 1, 1], entries.Select(e => e.Count));
        Assert.Equal(1, entries[0].Last.Id);
        Assert.Equal(T0.AddMinutes(-3), entries[0].Since);
        Assert.Equal(["vacancy-evaluation", "cover-letter"], entries[0].Purposes);
        Assert.Equal([LlmJournal.Error, LlmJournal.Ok, LlmJournal.Error, LlmJournal.Error, LlmJournal.Rejected, LlmJournal.Rejected],
            entries.Select(e => e.Outcome));
    }

    // Как на стенде: две задачи падают вперемешку с разными ошибками, время паузы в тексте меняется — две строки, а не 6
    [Fact]
    public void Collapse_InterleavedErrors_IgnoringNumbers()
    {
        var entries = LlmJournal.Collapse([
            Row(1, "сеть (host:443); пауза до 14:49 МСК", purpose: "cover-letter"), Row(2, "токен: сеть"),
            Row(3, "сеть (host:443); пауза до 14:44 МСК", purpose: "cover-letter"), Row(4, "токен: сеть"),
            Row(5, "сеть (host:443); пауза до 14:40 МСК", purpose: "cover-letter"), Row(6, "токен: сеть"),
        ]);

        Assert.Equal([(3, 1L), (3, 2L)], entries.Select(e => (e.Count, e.Last.Id)));
        Assert.Equal("сеть (host:443); пауза до 14:49 МСК", entries[0].Last.Error);
    }

    // Ошибки разных пользователей и моделей не сливаются: это разные строки в журнале администратора
    [Fact]
    public void Collapse_NotAcrossUsersOrModels()
    {
        var a = Guid.NewGuid();
        var entries = LlmJournal.Collapse([Row(1, "e", user: a), Row(2, "e"), Row(3, "e", model: "x")]);
        Assert.Equal(3, entries.Count);
    }

    [Theory]
    [InlineData("""{"verdict":"fit","score":82,"reason":"ok"}""", "подходит 82")]
    [InlineData("""{"verdict":"reject","score":30,"reason":"стек"}""", "не подходит 30")]
    [InlineData("не JSON", null)]
    public void Verdict_FromEvaluationResponse(string response, string? expected) =>
        Assert.Equal(expected, LlmJournal.Verdict("vacancy-evaluation", response));

    [Fact]
    public void Verdict_OnlyForEvaluation() => Assert.Null(LlmJournal.Verdict("cover-letter", """{"verdict":"fit","score":82}"""));

    [Theory]
    [InlineData("gsk_abcdefghijklmnopqrstuv1234", "…1234")]
    [InlineData("short-key", LlmKeyMask.Set)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void KeyMask_Last4OrSet(string? key, string? expected) => Assert.Equal(expected, LlmKeyMask.Of(key));

    [Theory]
    [InlineData("https://api.groq.com/openai/v1", "https://api.groq.com")]
    [InlineData("http://10.0.0.5:11434/v1?key=secret", "http://10.0.0.5:11434")]
    [InlineData("", null)]
    public void Host_WithoutPath(string url, string? expected) => Assert.Equal(expected, GetLlmServerConnectionsQueryHandler.Host(url));

    [Fact]
    public void LetterReason_WithoutPrefix() =>
        Assert.Equal("цифры не из фактов: 15", GetLlmLettersQueryHandler.Reason("письмо: проверить — цифры не из фактов: 15"));
}
