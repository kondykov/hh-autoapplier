using HhAutoApplier.Application.Llm;

namespace HhAutoApplier.Tests;

// Группы отсева и виды ошибок LLM по ключевым словам (#127)
public class LlmRejectGroupsTests
{
    [Theory]
    [InlineData("основной стек Go, C# — вторичный; опыта Go в профиле нет", LlmRejectGroups.Stack)]
    [InlineData("стек PHP, в профиле — C#/.NET", LlmRejectGroups.Stack)]
    [InlineData("Java-разработчик, Spring", LlmRejectGroups.Stack)]
    [InlineData("1С: программирование конфигураций", LlmRejectGroups.Stack)]
    [InlineData("нужен Senior с опытом от 6 лет", LlmRejectGroups.Level)]
    [InlineData("позиция тимлида, нужен опыт руководства", LlmRejectGroups.Level)]
    [InlineData("только офис в Москве", LlmRejectGroups.Office)]
    [InlineData("требуется переезд в Казань", LlmRejectGroups.Office)]
    [InlineData("больше поддержки, чем разработки", LlmRejectGroups.NotDev)]
    [InlineData("вакансия QA-инженера", LlmRejectGroups.NotDev)]
    [InlineData("поддержка 1С", LlmRejectGroups.NotDev)]
    [InlineData("мало данных в описании о стеке", LlmRejectGroups.Data)]
    [InlineData("описание пустое, требования не указаны", LlmRejectGroups.Data)]
    [InlineData("зарплата ниже ожиданий", LlmRejectGroups.Other)]
    [InlineData("", LlmRejectGroups.Other)]
    [InlineData(null, LlmRejectGroups.Other)]
    public void Classify_ByKeywords(string? reason, string group) => Assert.Equal(group, LlmRejectGroups.Classify(reason));

    // «Go» — словом: Google, Golang-подобные подстроки в других словах не путаются со стеком
    [Fact]
    public void Classify_ShortTokens_AsWords()
    {
        Assert.Equal(LlmRejectGroups.Other, LlmRejectGroups.Classify("компания похожа на Google-аутсорс с плохими отзывами"));
        Assert.Equal(LlmRejectGroups.Other, LlmRejectGroups.Classify("валидация требований не пройдена"));
    }

    [Fact]
    public void Titles_CoverAllGroups_OtherLast()
    {
        Assert.Equal(6, LlmRejectGroups.Titles.Count);
        Assert.Equal(LlmRejectGroups.Other, LlmRejectGroups.Titles[^1].Id);
        Assert.True(LlmRejectGroups.Exists("stack"));
        Assert.False(LlmRejectGroups.Exists("nope"));
    }

    [Theory]
    [InlineData("исчерпан дневной лимит запросов (300)", LlmErrorKinds.Limit)]
    [InlineData("провайдер ограничил частоту запросов; запросы к qwen приостановлены до 15:20 МСК", LlmErrorKinds.Limit)]
    [InlineData("The SSL connection could not be established", LlmErrorKinds.Network)]
    [InlineData("нет ответа за 60 с", LlmErrorKinds.Network)]
    [InlineData("ГигаЧат: получение токена, сеть: No such host", LlmErrorKinds.Network)]
    [InlineData("провайдер ответил 403: Access denied", LlmErrorKinds.Provider)]
    [InlineData("ГигаЧат отклонил ключ авторизации (401): проверьте LLM_API_KEY", LlmErrorKinds.Provider)]
    [InlineData("ответ не разобран", LlmErrorKinds.Other)]
    [InlineData(null, LlmErrorKinds.Other)]
    public void ErrorKinds(string? error, string kind) => Assert.Equal(kind, LlmErrorKinds.Classify(error));
}
