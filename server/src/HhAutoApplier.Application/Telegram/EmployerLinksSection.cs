using HhAutoApplier.Application.Commands.Dialogs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Queries.Dialogs;
using HhAutoApplier.Domain.Dialogs;

namespace HhAutoApplier.Application.Telegram;

public class EmployerLinksSection(GetEmployerLinksQueryHandler links, DecideEmployerLinkCommandHandler decide) : IBotSection
{
    public const string Show = "els";
    private const string Complete = "elc";
    private const string Dismiss = "eld";
    private const string Remind = "elr";

    public IReadOnlyCollection<string> Actions => [Show, Complete, Dismiss, Remind];

    public async Task<BotResult> PressAsync(BotPress press, CancellationToken ct)
    {
        if (press.Action == Show)
        {
            var data = await links.Handle(new GetEmployerLinksQuery(), ct);
            var pending = data.IsT0 ? data.AsT0.Where(link => link.State == EmployerLinkState.Pending).ToList() : [];
            var link = pending.FirstOrDefault();
            if (link is null)
                return BotResults.Show(new BotMessage("Нет анкет и тестов, которые ждут действий.")
                    { Rows = [[new BotButton("☰ Меню", $"{MenuSection.Show}:")]] });

            var rows = NotificationButtons(link.ChatId.ToString(), link.Key, link.Url).ToList();
            rows.Add([new BotButton("☰ Меню", $"{MenuSection.Show}:")]);
            return BotResults.Show(new BotMessage($"Ссылки вне hh.ru · {pending.Count}\n{link.Title}\n{link.Company}\n{link.Action ?? link.Domain}\n{link.Quote}")
                { Rows = rows });
        }

        if (!long.TryParse(press.Arg(0), out var chatId) || press.Arg(1) is not { } key)
            return BotResults.Notice("Ссылка не найдена");
        var decision = press.Action switch
        {
            Complete => EmployerLinkDecision.Complete,
            Dismiss => EmployerLinkDecision.Dismiss,
            _ => EmployerLinkDecision.Remind,
        };
        var result = await decide.Handle(new DecideEmployerLinkCommand(chatId, key, decision), ct);
        if (!result.IsT0)
            return BotResults.Notice(result.IsT1 ? "Ссылка не найдена" : "Напоминание уже отправлено");
        var status = result.AsT0.State switch
        {
            EmployerLinkState.Completed => "✓ Заполнено",
            EmployerLinkState.Dismissed => "Пропущено",
            _ => "Напомню завтра",
        };
        return new BotResult { Notice = status, Status = status };
    }

    public static IReadOnlyList<IReadOnlyList<BotButton>> NotificationButtons(string chatId, string key, string? url)
    {
        var links = new List<BotButton>();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            links.Add(BotButton.Link($"Открыть {uri.IdnHost} ↗", uri.AbsoluteUri));
        links.Add(BotButton.Link("💬 Чат на hh.ru", $"https://hh.ru/chat/{chatId}"));
        return
        [
            links,
            [new BotButton("Заполнил", $"{Complete}:{chatId}:{key}"), new BotButton("Не буду", $"{Dismiss}:{chatId}:{key}"),
                new BotButton("Напомнить завтра", $"{Remind}:{chatId}:{key}")],
        ];
    }
}
