using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Commands.Collecting;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Queries.Collecting;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// Сбор вакансий — только администратору, как CollectorController ([Authorize(Roles = Admin)]): состояние и «Запустить сейчас»
public class CollectionSection(GetCollectionStateQueryHandler state, StartCollectionCommandHandler start, ICurrentUser current) : IBotSection
{
    public const string Show = "cs";

    public const string Start = "cg";

    public const string AdminOnly = "Сбор — только для администратора";

    public IReadOnlyCollection<string> Actions => [Show, Start];

    public async Task<BotCallbackResult> PressAsync(BotPress press, CancellationToken ct)
    {
        if (!current.IsAdmin)
            return BotResults.Notice(AdminOnly);

        if (press.Action == Show)
            return BotResults.Edit(await ScreenAsync(ct));

        var started = (await start.Handle(new StartCollectionCommand(), ct)).Value is CollectionStateDto;
        return BotResults.Edit(await ScreenAsync(ct), started ? "Сбор запущен" : "Сбор уже идёт");
    }

    public async Task<BotMessage> ScreenAsync(CancellationToken ct)
    {
        if (!current.IsAdmin)
            return new BotMessage(AdminOnly);

        var s = await state.Handle(new GetCollectionStateQuery(), ct);
        var rows = new List<BotButton>();
        if (s.Current is null)
            rows.Add(new BotButton("▶️ Запустить сейчас", $"{Start}:"));
        rows.Add(new BotButton("🔄 Обновить", $"{Show}:"));
        return new BotMessage($"🛠 {B("Сбор вакансий")}\nИдёт сам раз в час; запущенный сбор продолжается на сервере.\n\n{E(Text(s))}")
        {
            Html = true,
            Rows = [rows],
        };
    }

    // Как collectionText в вебе
    public static string Text(CollectionStateDto state)
    {
        static string Counts(CollectionRunDto r) => $"просмотрено {r.Seen}, новых {r.Added}, в очередь {r.Queued}, отсеяно {r.Skipped}";
        if (state.Current is { } current)
        {
            var place = current.Search is null ? "" : $"поиск «{current.Search}», страница {current.Page}: ";
            return $"Идёт сбор ({current.Trigger}) с {Date(current.StartedAt)}. {place}{Counts(current)}";
        }

        if (state.Last is not { } last)
            return "С момента запуска сервера сбор ещё не завершался.";

        var error = last.Error is null ? "" : $"Остановлен: {last.Error}. ";
        return $"Последний сбор ({last.Trigger}) закончился {Date(last.FinishedAt)}. {error}{(last.Searches > 0 ? $"Поисков {last.Searches}, " : "")}{Counts(last)}";
    }
}
