using System.Text.RegularExpressions;

namespace HhAutoApplier.Application.Llm;

// Группы отсева LLM для вкладки «LLM → Отсев» (#127): по ключевым словам причины, без вызова LLM и без правки промпта оценки
// (решение 2026-11-19). Грубо, но дёшево: первая подошедшая группа по порядку, иначе — «Прочее»
public static class LlmRejectGroups
{
    public const string Data = "data";

    public const string NotDev = "notdev";

    public const string Office = "office";

    public const string Level = "level";

    public const string Stack = "stack";

    public const string Other = "other";

    public record Group(string Id, string Title, Regex Pattern);

    private static Regex Words(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // Порядок важен: «мало данных о стеке» — мало данных, «поддержка 1С» — не разработка, «офис, нужен senior» — офис
    public static readonly IReadOnlyList<Group> All =
    [
        new(Data, "Мало данных в описании",
            Words(@"мало (данных|информации|деталей|сведений)|недостаточно (данных|информации|деталей|сведений)|нет описания|описани[еяю] (пуст|скудн|размыт|общ)|неясн|непонятн|не указан")),
        new(NotDev, "Не разработка (поддержка, QA)",
            Words(@"поддержк|сопровожд|\bqa\b|тестировщ|тестирован|аналитик|devops|администрир|\bадмин|менеджер|продаж|преподава|не разработ|не программ|внедрен|техпис|технический писатель")),
        new(Office, "Офис / переезд",
            Words(@"офис|переезд|релокац|гибрид|очно|на месте|только в город|не удал[её]н|без удал[её]н|удал[её]нк[аи] нет|другом город")),
        new(Level, "Нужен уровень выше (Senior, Lead)",
            Words(@"senior|сеньор|синьор|\blead\b|\bлид\b|тимлид|техлид|ведущ|архитектор|руководител|head of|уровень|грейд|junior|джун|стаж[её]р|лет опыта|опыт от|опыта от|коммерческого опыта|опыта коммерческ|недостаточно опыта")),
        new(Stack, "Стек не совпадает (PHP, Java, 1С…)",
            Words(@"стек|технолог|\bphp\b|java\b|\b1[сc]\b|python|\bgo\b|golang|ruby|node|javascript|typescript|react|angular|\bvue\b|frontend|фронтенд|c\+\+|kotlin|swift|scala|rust|delphi|битрикс|\bязык")),
    ];

    public static readonly IReadOnlyList<(string Id, string Title)> Titles = [.. All.Select(g => (g.Id, g.Title)), (Other, "Прочее")];

    public static string Classify(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Other;

        return All.FirstOrDefault(g => g.Pattern.IsMatch(reason))?.Id ?? Other;
    }

    public static bool Exists(string? id) => Titles.Any(t => t.Id == id);
}

// Вид ошибки вызова LLM в журнале: лимит (свой дневной или частота у провайдера), сеть (VPN, SSL, таймаут), ответ провайдера (401/403/5xx)
public static class LlmErrorKinds
{
    public const string Limit = "limit";

    public const string Network = "network";

    public const string Provider = "provider";

    public const string Other = "other";

    public static string Classify(string? error)
    {
        var e = error?.ToLowerInvariant() ?? "";
        if (e.Contains("лимит") || e.Contains("429") || e.Contains("ограничил частоту") || e.Contains("доля запросов"))
            return Limit;

        if (e.Contains("сеть") || e.Contains("ssl") || e.Contains("нет ответа за") || e.Contains("timeout") || e.Contains("timed out")
            || e.Contains("connection") || e.Contains("подключ") || e.Contains("proxy") || e.Contains("прокси") || e.Contains("name or service"))
            return Network;

        if (e.Contains("ответил") || e.Contains("отклонил") || e.Contains("403") || e.Contains("401") || e.Contains("forbidden"))
            return Provider;

        return Other;
    }
}
