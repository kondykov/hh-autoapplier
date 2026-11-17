using System.Text;
using HhAutoApplier.Application.Commands.Dialogs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Queries.Dialogs;
using HhAutoApplier.Domain.Dialogs;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// «Ответы работодателей», как dialogCard в вебе: необработанные (кроме отказов) или все, карточка с черновиком в <pre>
// (копируется нажатием) вместо «Копировать», кнопка-ссылка на чат hh.ru, «Обработано». Фильтр в кнопках: o — открытые, a — все
public class DialogsSection(
    GetDialogsQueryHandler dialogs,
    GetDialogQueryHandler dialog,
    MarkDialogHandledCommandHandler markHandled
) : IBotSection
{
    public const string List = "dl";

    public const string Card = "dc";

    public const string Handled = "dh";

    public const int PageSize = 5;

    public IReadOnlyCollection<string> Actions => [List, Card, Handled, BotTexts.Handled];

    public async Task<BotResult> PressAsync(BotPress press, CancellationToken ct)
    {
        var skip = int.TryParse(press.Arg(press.Action == List ? 0 : 1), out var s) ? Math.Max(s, 0) : 0;
        var all = press.Arg(press.Action == List ? 1 : 2) == "a";
        _ = long.TryParse(press.Arg(0), out var chatId);
        switch (press.Action)
        {
            case List:
                return BotResults.Show(await ListAsync(skip, all, ct));
            case Card:
                return (await dialog.Handle(new GetDialogQuery(chatId), ct)).Value is EmployerMessageDto m
                    ? BotResults.Show(CardScreen(m, skip, all))
                    : BotResults.Notice("Чат не найден");
            case Handled:
                return (await markHandled.Handle(new MarkDialogHandledCommand(chatId), ct)).Value is EmployerMessageDto done
                    ? BotResults.Show(CardScreen(done, skip, all), "Обработано")
                    : BotResults.Notice("Чат не найден");
            default:
                // Кнопка уведомления: дописать итог к сообщению
                return (await markHandled.Handle(new MarkDialogHandledCommand(chatId), ct)).Value is EmployerMessageDto
                    ? new BotResult { Notice = "Обработано", Status = "✔ Обработано" }
                    : BotResults.Notice("Чат не найден");
        }
    }

    private static string Kind(DialogKind? kind) => kind switch
    {
        DialogKind.Invitation => "Приглашение",
        DialogKind.Question => "Вопрос",
        DialogKind.Rejection => "Отказ",
        DialogKind.Other => "Прочее",
        _ => "ждёт разбора",
    };

    private static string Filter(bool all) => all ? "a" : "o";

    public async Task<BotMessage> ListAsync(int skip, bool all, CancellationToken ct)
    {
        var query = new GetDialogsQuery { Handled = all ? null : false, Take = 500 };
        var items = ((await dialogs.Handle(query, ct)).Value is List<EmployerMessageDto> l ? l : [])
            .Where(m => all || m.Kind != DialogKind.Rejection).ToList();
        var page = items.Skip(skip).Take(PageSize).ToList();

        var text = new StringBuilder($"💬 {B("Ответы работодателей")} · {(all ? "все" : "необработанные, кроме отказов")}: {items.Count}");
        if (page.Count == 0)
            text.Append(all
                ? "\n\nСообщений пока нет: нажмите «Автоответ» в панели расширения на странице чатов hh.ru."
                : "\n\nНовых ответов работодателей нет.");
        foreach (var (m, i) in page.Select((m, i) => (m, i)))
        {
            var meta = string.Join(" · ", new[] { m.Company, Date(m.At), m.HandledAt is null ? "" : "обработано" }.Where(x => x != ""));
            text.Append($"\n\n{skip + i + 1}. {B(Kind(m.Kind))}: {E(Cut(m.Title is { Length: > 0 } t ? t : "чат без вакансии", 80))}\n{E(meta)}");
            if (!string.IsNullOrWhiteSpace(m.Summary))
                text.Append($"\n{I(Cut(m.Summary, 200))}");
        }

        var rows = page.Select((m, i) => (IReadOnlyList<BotButton>)[new BotButton($"{skip + i + 1}. {Cut(m.Title is { Length: > 0 } t ? t : "чат", 40)}", $"{Card}:{m.ChatId}:{skip}:{Filter(all)}")]).ToList();
        var nav = new List<BotButton>();
        if (skip > 0)
            nav.Add(new BotButton("← Назад", $"{List}:{Math.Max(0, skip - PageSize)}:{Filter(all)}"));
        if (items.Count > skip + PageSize)
            nav.Add(new BotButton("Дальше →", $"{List}:{skip + PageSize}:{Filter(all)}"));
        if (nav.Count > 0)
            rows.Add(nav);
        rows.Add([all ? new BotButton("Только необработанные", $"{List}:0:o") : new BotButton("Показать все", $"{List}:0:a"), new BotButton("☰ Меню", $"{MenuSection.Show}:")]);
        return new BotMessage(text.ToString()) { Html = true, Rows = rows };
    }

    private static BotMessage CardScreen(EmployerMessageDto m, int skip, bool all)
    {
        var chatUrl = $"https://hh.ru/chat/{m.ChatId}";
        var autoReply = m.AutoReply switch
        {
            AutoReplyState.Pending => "автоответ ждёт отправки",
            AutoReplyState.Sent => "автоответ отправлен",
            AutoReplyState.Unavailable => "автоответ невозможен",
            _ => "",
        };
        var meta = new[] { m.Company, Date(m.At), m.HhState is { } h ? $"hh: {h}" : "", autoReply, m.AutoReplyError ?? "", m.HandledAt is null ? "" : "обработано" };
        var text = new StringBuilder($"💬 {B(Kind(m.Kind))}: <b>{Link(m.Title is { Length: > 0 } t ? t : "Чат без вакансии", chatUrl)}</b>\n")
            .Append(E(string.Join(" · ", meta.Where(x => !string.IsNullOrWhiteSpace(x)))));
        if (!string.IsNullOrWhiteSpace(m.Summary))
            text.Append($"\n\n{B("Суть")}: {E(Cut(m.Summary, 500))}");
        text.Append($"\n\n{B("Сообщение")}\n{Quote(Cut(m.Text, 1500))}");
        if (m.Kind != DialogKind.Rejection)
            text.Append(string.IsNullOrWhiteSpace(m.SuggestedReply)
                ? $"\n\n{I(m.Kind is null ? "LLM ещё не разобрала сообщение — откройте карточку через минуту." : "Черновика нет.")}"
                : $"\n\n{B($"Черновик ответа{(m.LlmModel is null ? "" : $" ({m.LlmModel})")}")} — нажмите, чтобы скопировать, поправьте [заглушки] и отправьте в чате:\n{Pre(Cut(m.SuggestedReply, 2000))}");

        var actions = new List<BotButton> { BotButton.Link("💬 Открыть чат", chatUrl) };
        if (m.HandledAt is null)
            actions.Add(new BotButton("✔ Обработано", $"{Handled}:{m.ChatId}:{skip}:{Filter(all)}"));
        return new BotMessage(text.ToString())
        {
            Html = true,
            Rows = [actions, [new BotButton("← К списку", $"{List}:{skip}:{Filter(all)}")]],
        };
    }
}
