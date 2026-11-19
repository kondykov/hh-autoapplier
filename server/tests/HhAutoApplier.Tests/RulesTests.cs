using HhAutoApplier.Domain.Resumes;
using HhAutoApplier.Domain.Searches;
using HhAutoApplier.Domain.Services;
using HhAutoApplier.Domain.Vacancies;

namespace HhAutoApplier.Tests;

public class RulesTests
{
    private static readonly ResumeProfile[] Resumes =
    [
        ResumeProfile.Create(".NET", "C# .NET", ["c#", ".net", "asp.net"]),
        ResumeProfile.Create("PHP", "PHP", ["php", "laravel"]),
    ];

    private static Vacancy V(string title, string desc = "", int? from = null, string cur = "RUR", string fmt = "")
    {
        var v = Vacancy.Create("1", title, "https://hh.ru/vacancy/1", "test", DateTimeOffset.UtcNow);
        v.UpdateCard(null, null, Salary.From(from, null, cur), fmt, null, null);
        v.UpdateDetails(null, desc, []);
        return v;
    }

    [Fact]
    public void Picker_ChoosesByKeywords_NullOnTieOrNone()
    {
        Assert.Equal(".NET", ResumePicker.Pick(Resumes, "Backend C# .NET developer")?.Name);
        Assert.Equal("PHP", ResumePicker.Pick(Resumes, "PHP Laravel")?.Name);
        Assert.Null(ResumePicker.Pick(Resumes, "php и c#"));
        Assert.Null(ResumePicker.Pick(Resumes, "Менеджер"));
    }

    [Fact]
    public void Stack_ExtractedFromTextAndSkills()
    {
        var stack = StackExtractor.Extract("Разработчик на C# и ASP.NET, PostgreSQL, Docker", ["Kafka"]);
        Assert.Contains("C#", stack);
        Assert.Contains(".NET", stack);
        Assert.Contains("SQL", stack);
        Assert.Contains("Kafka", stack);
        Assert.DoesNotContain("Java", stack);
    }

    // Границы слова — у всех вариантов шаблона: короткие «go», «ts», «js», «git» не находятся внутри слов
    [Theory]
    [InlineData("Python, Django", "Go")]
    [InlineData("Работа с клиентами, clients и experts", "TypeScript")]
    [InlineData("Legit компания, nodejs не нужен", "Git")]
    [InlineData("Golang не нужен, только Django", "JavaScript")]
    public void Stack_ShortNamesOnlyAsWholeWords(string text, string absent) =>
        Assert.DoesNotContain(absent, StackExtractor.Extract(text));

    [Theory]
    [InlineData("Бэкенд на Go и gRPC", "Go")]
    [InlineData("Golang-разработчик", "Go")]
    [InlineData("React + TS", "TypeScript")]
    [InlineData("Node.js, JS", "JavaScript")]
    [InlineData("Git, GitLab CI", "Git")]
    [InlineData("MySQL и MSSQL", "SQL")]
    public void Stack_ShortNamesFound(string text, string present) =>
        Assert.Contains(present, StackExtractor.Extract(text));

    [Fact]
    public void Evaluate_ExcludeKeyword_Skips()
    {
        var rules = new FilterRules { ExcludeKeywords = ["Стажёр"] };
        var e = VacancyEvaluator.Default.Evaluate(V("Стажер C#"), rules, Resumes);
        Assert.Equal(VacancyStatus.Skipped, e.Status);
    }

    // #112: ключевые слова — по границам слова, а не по подстроке
    [Theory]
    [InlineData("java", "Frontend-разработчик JavaScript", false)]
    [InlineData("java", "Java developer", true)]
    [InlineData("lead", "Leading backend developer", false)]
    [InlineData("lead", "Team Lead .NET", true)]
    [InlineData("go", "Django developer", false)]
    [InlineData("go", "Go/Python разработчик", true)]
    [InlineData("стажер", "Ищем стажеров в команду", true)]
    [InlineData("стажёр", "Стажёр C#", true)]
    [InlineData("1С", "Программист 1С-Битрикс", true)]
    [InlineData("Битрикс", "Разработчик Битрикс24", true)]
    [InlineData("c#", "Разработчик C#/.NET", true)]
    [InlineData(".net", "Разработчик ASP.NET Core", true)]
    [InlineData("c++", "Разработчик C++", true)]
    [InlineData("c++", "Разработчик C", false)]
    [InlineData("team lead", "Team  Lead Python", true)]
    public void Keyword_MatchesWordNotSubstring(string keyword, string text, bool expected) =>
        Assert.Equal(expected, KeywordMatcher.Matches(TextNormalizer.Normalize(text), keyword));

    [Fact]
    public void Evaluate_ExcludeKeyword_JavaDoesNotSkipJavaScript()
    {
        var rules = new FilterRules { ExcludeKeywords = ["java"] };
        Assert.Equal(VacancyStatus.Queued, VacancyEvaluator.Default.Evaluate(V("JavaScript разработчик"), rules, Resumes).Status);
        Assert.Equal(VacancyStatus.Skipped, VacancyEvaluator.Default.Evaluate(V("Java разработчик"), rules, Resumes).Status);
    }

    // Пустое слово не отсеивает всё подряд и не считается нужным словом
    [Fact]
    public void Evaluate_EmptyKeywords_Ignored()
    {
        Assert.Equal(VacancyStatus.Queued, VacancyEvaluator.Default.Evaluate(V("C#"), new FilterRules { ExcludeKeywords = ["", "  "] }, Resumes).Status);
        Assert.Equal(VacancyStatus.Queued, VacancyEvaluator.Default.Evaluate(V("C#"), new FilterRules { IncludeKeywords = [""] }, Resumes).Status);
    }

    [Fact]
    public void Evaluate_IncludeKeyword_ByWord()
    {
        var rules = new FilterRules { IncludeKeywords = ["java"] };
        var e = VacancyEvaluator.Default.Evaluate(V("JavaScript разработчик"), rules, Resumes);
        Assert.Equal(VacancyStatus.Skipped, e.Status);
        Assert.Equal("нет ни одного нужного слова", e.Reason);
    }

    [Fact]
    public void Evaluate_SalaryConvertedToRub()
    {
        var rules = new FilterRules { MinSalaryRub = 200_000 };
        Assert.Equal(VacancyStatus.Queued, VacancyEvaluator.Default.Evaluate(V("C#", from: 3000, cur: "USD"), rules, Resumes).Status);
        Assert.Equal(VacancyStatus.Skipped, VacancyEvaluator.Default.Evaluate(V("C#", from: 100_000), rules, Resumes).Status);
        Assert.Equal(VacancyStatus.Queued, VacancyEvaluator.Default.Evaluate(V("C#"), rules, Resumes).Status);

        var strict = new FilterRules { MinSalaryRub = 200_000, RequireSalary = true };
        Assert.Equal(VacancyStatus.Skipped, VacancyEvaluator.Default.Evaluate(V("C#"), strict, Resumes).Status);
    }

    [Fact]
    public void Evaluate_FormatAndResume()
    {
        var rules = new FilterRules { AllowedFormats = ["удален"] };
        Assert.Equal(VacancyStatus.Skipped, VacancyEvaluator.Default.Evaluate(V("C#", fmt: "На месте работодателя"), rules, Resumes).Status);
        var ok = VacancyEvaluator.Default.Evaluate(V("C# .NET", fmt: "Удалённо"), rules, Resumes);
        Assert.Equal(VacancyStatus.Queued, ok.Status);
        Assert.Equal(".NET", ok.ResumeProfile);
    }
}
