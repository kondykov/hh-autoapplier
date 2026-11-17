using System.Text;
using HhAutoApplier.Application.Commands.Searches;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.Searches;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// «Поиски»: список с состоянием, включить и выключить (решение владельца 2026-11-17: правила — только в вебе).
// Переключение — та же SaveSearchCommand, что у веба, с прежними запросом и правилами
public class SearchesSection(GetSearchesQueryHandler searches, SaveSearchCommandHandler save) : IBotSection
{
    public const string Show = "sl";

    public const string Toggle = "st";

    public IReadOnlyCollection<string> Actions => [Show, Toggle];

    public async Task<BotResult> PressAsync(BotPress press, CancellationToken ct)
    {
        if (press.Action == Show)
            return BotResults.Show(await ScreenAsync(ct));

        var list = await ListAsync(ct);
        // id из кнопки ищется среди своих поисков: чужой не найти
        if (!int.TryParse(press.Arg(0), out var id) || list.FirstOrDefault(s => s.Id == id) is not { } search)
            return BotResults.Notice("Поиск не найден");

        var command = new SaveSearchCommand
        {
            Name = search.Name,
            Query = search.Query,
            Area = search.Area,
            ExtraParams = search.ExtraParams,
            Enabled = !search.Enabled,
            Rules = search.Rules,
        };
        var result = (await save.Handle(command, ct)).Value;
        if (result is not SearchDto saved)
            return BotResults.Notice(Cut(result is Error e ? e.Message : "Не удалось", 190));

        return BotResults.Show(await ScreenAsync(ct), saved.Enabled ? "Включён" : "Выключен");
    }

    private async Task<List<SearchDto>> ListAsync(CancellationToken ct) =>
        (await searches.Handle(new GetSearchesQuery(), ct)).Value is List<SearchDto> list ? list : [];

    public async Task<BotMessage> ScreenAsync(CancellationToken ct)
    {
        var list = await ListAsync(ct);
        var text = new StringBuilder($"🔎 {B("Поиски")}");
        if (list.Count == 0)
            text.Append("\n\nПоисков нет: добавьте поиск в веб-интерфейсе.");
        foreach (var s in list)
            text.Append($"\n\n{(s.Enabled ? "✅" : "⏸")} {B(s.Name)}\n{E(Cut(s.Query, 200))}{(string.IsNullOrWhiteSpace(s.Area) ? "" : E($" · регион {s.Area}"))}");
        text.Append($"\n\n{I("Выключенный поиск сбор пропускает. Запрос и правила отбора меняются в веб-интерфейсе.")}");

        var rows = list.Select(s => (IReadOnlyList<BotButton>)[new BotButton($"{(s.Enabled ? "⏸ Выключить" : "▶️ Включить")} «{Cut(s.Name, 30)}»", $"{Toggle}:{s.Id}")]).ToList();
        rows.Add([new BotButton("🔄 Обновить", $"{Show}:"), new BotButton("☰ Меню", $"{MenuSection.Show}:")]);
        return new BotMessage(text.ToString()) { Html = true, Rows = rows };
    }
}
