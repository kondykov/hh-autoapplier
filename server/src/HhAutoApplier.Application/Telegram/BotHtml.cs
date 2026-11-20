using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Vacancies;

namespace HhAutoApplier.Application.Telegram;

// Разметка HTML Telegram для экранов бота (#115). Всё, что пришло от работодателя, LLM или человека, — только через E:
// Telegram понимает лишь &lt; &gt; &amp;, неэкранированный «<» в названии вакансии сломал бы сообщение
public static partial class BotHtml
{
    // Лимит текста сообщения Telegram
    public const int MaxText = 4096;

    public static string E(string? text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Обрезать длинный текст с пометкой; дальше — ссылкой на hh.ru или кнопкой
    public static string Cut(string? text, int max)
    {
        var s = (text ?? "").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    public static string B(string? text) => $"<b>{E(text)}</b>";

    public static string I(string? text) => $"<i>{E(text)}</i>";

    // Блок, который копируется нажатием: черновики ответов и письма
    public static string Pre(string text) => $"<pre>{E(text)}</pre>";

    // Свёрнутая цитата: длинное письмо или сообщение не занимает весь экран
    public static string Quote(string text) => $"<blockquote expandable>{E(text)}</blockquote>";

    // Ссылка — только http(s): адрес приходит со страницы работодателя
    public static string Link(string? text, string? url) =>
        SafeUrl(url) is { } safe ? $"<a href=\"{E(safe).Replace("\"", "&quot;")}\">{E(text)}</a>" : E(text);

    public static string? SafeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;

    // Длина текста, который увидит Telegram, — без тегов и с раскрытыми &amp;
    public static int VisibleLength(string html) => WebUtility.HtmlDecode(Tags().Replace(html, "")).Length;

    public static string Plain(string html) => WebUtility.HtmlDecode(Tags().Replace(html, ""));

    // Не помещается в сообщение — без разметки и обрезано: так Telegram примет его в любом случае
    public static BotMessage Fit(BotMessage message)
    {
        if (message.Html ? VisibleLength(message.Text) <= MaxText : message.Text.Length <= MaxText)
            return message;

        var plain = message.Html ? Plain(message.Text) : message.Text;
        const string cut = "\n…(обрезано: полностью — в веб-интерфейсе)";
        return message with { Text = plain[..(MaxText - cut.Length)].TrimEnd() + cut, Html = false };
    }

    // Время для людей — московское, как в уведомлениях (#108)
    public static string Date(DateTimeOffset? at) => at is { } t ? $"{t.ToOffset(TimeSpan.FromHours(3)):dd.MM HH:mm}" : "";

    public static string Salary(VacancyDto v)
    {
        if (v.SalaryFrom is null && v.SalaryTo is null)
            return "";

        var parts = new[] { v.SalaryFrom is { } from ? $"от {Number(from)}" : "", v.SalaryTo is { } to ? $"до {Number(to)}" : "" };
        return $"{string.Join(" ", parts.Where(p => p != ""))} {v.SalaryCurrency}".Trim();
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    public static string Status(VacancyStatus status) => status switch
    {
        VacancyStatus.New => "новая",
        VacancyStatus.Pending => "ждёт оценки",
        VacancyStatus.Review => "на проверке",
        VacancyStatus.Questionnaire => "анкета",
        VacancyStatus.Queued => "в очереди",
        VacancyStatus.Applied => "отклик отправлен",
        VacancyStatus.Invited => "приглашение",
        VacancyStatus.Interview => "собеседование",
        VacancyStatus.Rejected => "отказ",
        VacancyStatus.Skipped => "пропущена",
        VacancyStatus.Failed => "ошибка",
        _ => status.ToString(),
    };

    // Ряды кнопок по count в ряд
    public static IReadOnlyList<IReadOnlyList<BotButton>> Rows(IEnumerable<BotButton> buttons, int count) =>
        buttons.Chunk(count).Select(r => (IReadOnlyList<BotButton>)r).ToList();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();
}
