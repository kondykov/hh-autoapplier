using System.Text.RegularExpressions;
using HhAutoApplier.Application.Dtos;

namespace HhAutoApplier.Application.Telegram;

// Сообщения бота, на которые отвечают текстом (#115, решение владельца 2026-11-17: без хранения состояния и миграции).
// Первая строка — «вид · #id вакансии»: по ней ответ привязывается к вакансии. Верить ей можно только в сообщении самого бота
// (BotSource.FromBot); вакансия ищется среди своих откликов, так что чужой id ничего не откроет
public static partial class BotPrompt
{
    public const string Letter = "✏️ Письмо";

    public const string Draft = "📝 Черновик";

    public const string DeclineReason = "❌ Причина отказа";

    public const string SkipReason = "⏭ Причина пропуска";

    private const string AnswerPrefix = "✏️ Ответ ";

    public static string Answer(int number) => $"{AnswerPrefix}{number}";

    public static string Header(string kind, string vacancyId) => BotHtml.E($"{kind} · #{vacancyId}");

    // Номер ответа анкеты из вида «✏️ Ответ N»; null — другой вид
    public static int? AnswerNumber(string kind) =>
        kind.StartsWith(AnswerPrefix, StringComparison.Ordinal) && int.TryParse(kind[AnswerPrefix.Length..], out var n) ? n : null;

    public static (string Kind, string VacancyId)? Parse(BotSource? source)
    {
        if (source is not { FromBot: true })
            return null;

        var match = HeaderLine().Match(source.Text.Split('\n', 2)[0].Trim());
        return match.Success ? (match.Groups["kind"].Value, match.Groups["id"].Value) : null;
    }

    [GeneratedRegex(@"^(?<kind>.+?) · #(?<id>\S+)$")]
    private static partial Regex HeaderLine();
}
