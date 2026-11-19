using System.Text.RegularExpressions;

namespace HhAutoApplier.Domain.Services;

// Ключевые слова правил поиска (#112): совпадение по границам слова, а не по подстроке — «java» не находит JavaScript,
// «lead» — leading. Латинское слово — целиком; русское — началом слова, чтобы «стажер» находил «стажера» и «стажеров».
// Граница проверяется только у буквы или цифры на краю слова: «.net» находит asp.net, «c#» и «c++» — как написаны
public static class KeywordMatcher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    // text — уже через TextNormalizer.Normalize; пустое слово не совпадает ни с чем
    public static bool Matches(string text, string? keyword)
    {
        var word = TextNormalizer.Normalize(keyword);
        if (word.Length == 0)
            return false;

        var start = char.IsLetterOrDigit(word[0]) ? @"(?<![\p{L}\d])" : "";
        var last = word[^1];
        var end = char.IsAsciiLetterOrDigit(last) ? @"(?![\p{L}\d])" : "";
        return Regex.IsMatch(text, start + Regex.Escape(word) + end, RegexOptions.CultureInvariant, Timeout);
    }

    public static string? FirstMatch(string text, IEnumerable<string> keywords) => keywords.FirstOrDefault(k => Matches(text, k));
}
