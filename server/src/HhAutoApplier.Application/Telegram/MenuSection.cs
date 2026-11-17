using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Queries.Vacancies;
using HhAutoApplier.Domain.Vacancies;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// Экран-меню (#115): вход во все разделы, справка и отвязка чата с подтверждением
public class MenuSection(GetStatsQueryHandler stats, BotDigest digest, BotChat chat, ICurrentUser current) : IBotSection
{
    public const string Show = "menu";

    public const string Help = "help";

    public const string Stop = "stop";

    public IReadOnlyCollection<string> Actions => [Show, Help, Stop, BotTexts.Unlink, BotTexts.Cancel];

    public async Task<BotResult> PressAsync(BotPress press, CancellationToken ct)
    {
        switch (press.Action)
        {
            case Help:
                return BotResults.Show(HelpScreen());
            case Stop:
                return BotResults.Show(StopScreen());
            case BotTexts.Unlink:
                chat.Current!.Unlink();
                await chat.SaveAsync(ct);
                return BotResults.Show(new BotMessage("Чат отвязан: уведомлений больше не будет. Привязать снова — новым кодом: /start <код>"), "Чат отвязан")
                    with { CommandsMenu = BotCommandsMenu.Stranger };
            default:
                return BotResults.Show(await ScreenAsync(ct), press.Action == BotTexts.Cancel ? "Отменено" : "");
        }
    }

    public async Task<BotMessage> ScreenAsync(CancellationToken ct)
    {
        var counts = (await stats.Handle(new GetStatsQuery(), ct)).Value is Dictionary<string, int> d ? d : [];
        var review = counts.GetValueOrDefault(VacancyStatus.Review.ToString());
        var dialogs = await digest.OpenDialogsAsync(ct);
        var rows = new List<IReadOnlyList<BotButton>>
        {
            new[] { new BotButton(review > 0 ? $"📝 Проверка ({review})" : "📝 Проверка", $"{ReviewSection.Card}:0"),
                new BotButton(dialogs > 0 ? $"💬 Ответы ({dialogs})" : "💬 Ответы", $"{DialogsSection.List}:0:o") },
            new[] { new BotButton("📊 Обзор", $"{OverviewSection.Show}:"), new BotButton("📋 Вакансии", $"{VacanciesSection.Menu}:") },
            current.IsAdmin
                ? new[] { new BotButton("🔎 Поиски", $"{SearchesSection.Show}:"), new BotButton("🛠 Сбор", $"{CollectionSection.Show}:") }
                : new[] { new BotButton("🔎 Поиски", $"{SearchesSection.Show}:") },
            new[] { new BotButton("❓ Помощь", $"{Help}:") },
        };
        var text = $"☰ {B("hh-autoapplier")}\nНа проверке: {review} · ответов работодателей: {dialogs} · в очереди: {counts.GetValueOrDefault(VacancyStatus.Queued.ToString())}";
        return new BotMessage(text) { Html = true, Rows = rows };
    }

    public static BotMessage HelpScreen() => new(BotTexts.Help) { Rows = [[new BotButton("☰ Меню", $"{Show}:")]] };

    public static BotMessage StopScreen() =>
        new("Отвязать чат? Уведомления перестанут приходить, привязать снова можно только новым кодом.",
            [new BotButton("Отвязать", $"{BotTexts.Unlink}:"), new BotButton("Отмена", $"{BotTexts.Cancel}:")]);
}
