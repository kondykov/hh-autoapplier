using System.Text;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Queries.Reports;
using HhAutoApplier.Application.Queries.Vacancies;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using static HhAutoApplier.Application.Telegram.BotHtml;

namespace HhAutoApplier.Application.Telegram;

// «Обзор», как в вебе: счётчики по статусам — кнопки в списки, «что дальше», отчёт CSV файлом; администратору — сбор
public class OverviewSection(
    GetStatsQueryHandler stats,
    GetApplicationsReportQueryHandler report,
    IAppDbContext db,
    ICurrentUser current,
    TimeProvider clock
) : IBotSection
{
    public const string Show = "ov";

    public const string Report = "csv";

    // Порядок плиток — как в веб-интерфейсе
    private static readonly VacancyStatus[] Order =
    [
        VacancyStatus.Queued, VacancyStatus.Review, VacancyStatus.Questionnaire, VacancyStatus.Pending, VacancyStatus.Applied,
        VacancyStatus.Invited, VacancyStatus.Rejected, VacancyStatus.Skipped, VacancyStatus.Failed, VacancyStatus.New,
    ];

    public IReadOnlyCollection<string> Actions => [Show, Report];

    public async Task<BotCallbackResult> PressAsync(BotPress press, CancellationToken ct) => press.Action switch
    {
        Report => BotResults.Reply(await ReportAsync(ct), "Отчёт"),
        _ => BotResults.Edit(await ScreenAsync(ct)),
    };

    public async Task<BotMessage> ScreenAsync(CancellationToken ct)
    {
        var counts = (await stats.Handle(new GetStatsQuery(), ct)).Value is Dictionary<string, int> d ? d : [];
        int Count(VacancyStatus status) => counts.GetValueOrDefault(status.ToString());
        var since = clock.GetUtcNow().AddDays(-1);
        var applied = await db.JobApplications.CountAsync(a => a.UserId == current.Id && a.AppliedAt >= since, ct);

        var text = new StringBuilder($"📊 {B("Обзор")}\n");
        var present = Order.Where(s => Count(s) > 0).ToList();
        if (present.Count == 0)
            text.Append("\nОткликов пока нет: добавьте поиск в веб-интерфейсе — сервер соберёт вакансии.\n");
        else
        {
            text.Append($"Всего откликов в работе: {counts.Values.Sum()}, за сутки отправлено: {applied}\n\n");
            foreach (var status in present)
                text.Append($"{E(Status(status))}: {Count(status)}\n");
        }

        var next = new List<string>();
        if (Count(VacancyStatus.Review) > 0)
            next.Add($"На проверке {Count(VacancyStatus.Review)} — решить");
        if (Count(VacancyStatus.Failed) > 0)
            next.Add($"С ошибкой {Count(VacancyStatus.Failed)} — вернуть в очередь или оставить");
        if (Count(VacancyStatus.Skipped) > 0)
            next.Add($"Пропущено {Count(VacancyStatus.Skipped)} (LLM, правила или вы) — пересмотреть и вернуть нужные в очередь");
        if (Count(VacancyStatus.Queued) > 0)
            next.Add($"В очереди {Count(VacancyStatus.Queued)} — лишние можно пропустить");
        next.Add("Ответы работодателей — приглашения и вопросы с черновиками");
        text.Append($"\n{B("Что дальше")}\n").Append(string.Join("\n", next.Select(n => $"• {E(n)}")));

        var rows = new List<IReadOnlyList<BotButton>>();
        if (Count(VacancyStatus.Review) > 0)
            rows.Add([new BotButton($"📝 Решить проверку ({Count(VacancyStatus.Review)})", $"{ReviewSection.Card}:0")]);
        rows.AddRange(Rows(present.Select(s => new BotButton($"{Status(s)}: {Count(s)}", $"{VacanciesSection.List}:{VacanciesSection.Context((int)s, 0, 0)}")), 2));
        rows.Add([new BotButton("💬 Ответы работодателей", $"{DialogsSection.List}:0:o"), new BotButton("📄 Отчёт CSV", $"{Report}:")]);
        rows.Add(current.IsAdmin
            ? [new BotButton("🛠 Сбор", $"{CollectionSection.Show}:"), new BotButton("🔄 Обновить", $"{Show}:")]
            : [new BotButton("🔄 Обновить", $"{Show}:")]);
        return new BotMessage(text.ToString().TrimEnd()) { Html = true, Rows = rows };
    }

    // Отчёт по отправленным откликам — тот же CSV, что по ссылке в вебе
    public async Task<BotMessage> ReportAsync(CancellationToken ct)
    {
        var result = (await report.Handle(new GetApplicationsReportQuery { Format = "csv" }, ct)).Value;
        return result is FileDto file
            ? new BotMessage("Отчёт по отправленным откликам (CSV, открывается в Excel)") { File = new BotFile(file.Content, file.FileName) }
            : new BotMessage("Отчёт недоступен");
    }
}
