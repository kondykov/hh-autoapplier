using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Queries.Dashboard;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;

namespace HhAutoApplier.Tests;

// Дашборд (#121): воронка, ключевые числа, ряды и разрезы на наборе откликов — без базы
public class DashboardMathTests
{
    // 18.11 15:00 МСК
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly DashboardLlmDto Llm = new(0, 0, 150);

    private static readonly DashboardChatsDto Chats = new(null, 0);

    private static DateTimeOffset Ago(double days) => Now.AddDays(-days);

    private static DashboardRow Row(double collected, string search = "dotnet", VacancyStatus? status = null, string? reason = null,
        int? llm = null, string source = "hh", params (double Ago, VacancyStatus To)[] path)
    {
        var history = new List<ApplicationEvent> { new() { At = Ago(collected), To = VacancyStatus.New } };
        history.AddRange(path.Select(p => new ApplicationEvent { At = Ago(p.Ago), To = p.To }));
        var applied = path.Where(p => p.To == VacancyStatus.Applied).Select(p => (DateTimeOffset?)Ago(p.Ago)).FirstOrDefault();
        return new DashboardRow(Ago(collected), status ?? history[^1].To, reason, llm, applied, search, source, history);
    }

    private static readonly DashboardRow[] Rows =
    [
        // Собрана 5 дней назад: очередь, отправка, приглашение, собеседование
        Row(5, llm: 80, path: [(5, VacancyStatus.Pending), (4, VacancyStatus.Queued), (3, VacancyStatus.Applied), (2, VacancyStatus.Invited), (1, VacancyStatus.Interview)]),
        // 10 дней назад: отправлена, сразу собеседование (без приглашения)
        Row(10, llm: 75, path: [(9, VacancyStatus.Queued), (8, VacancyStatus.Applied), (6, VacancyStatus.Interview)]),
        // 12 дней назад: отправлена, отказ
        Row(12, llm: 60, path: [(11, VacancyStatus.Queued), (11, VacancyStatus.Applied), (10, VacancyStatus.Rejected)]),
        // Отсеяны правилами и LLM
        Row(2, "backend", reason: "оценка 40 ниже 70: стек", path: [(2, VacancyStatus.Skipped)]),
        Row(3, "backend", reason: "оценка 55 ниже 70: уровень", path: [(3, VacancyStatus.Skipped)]),
        Row(3, "backend", reason: "стоп-слово «1С» в названии", path: [(3, VacancyStatus.Skipped)]),
        // Прошлый период: собрана 40 дней назад, отправлена 39, приглашение — уже в этом периоде
        Row(40, path: [(39, VacancyStatus.Applied), (20, VacancyStatus.Invited)]),
        // Другой агрегатор
        Row(1, "hirify-search", source: "hirify", path: [(1, VacancyStatus.Queued)]),
    ];

    [Fact]
    public void Totals_ByEventTime_WithPreviousPeriod()
    {
        var d = DashboardMath.Build(Rows, 30, "hh", Now, Llm, Chats);

        Assert.Equal(new DashboardTotalsDto(Collected: 6, PrevCollected: 1, Sent: 3, PrevSent: 1, Invited: 3, PrevInvited: 0,
            Interviews: 2, PrevInterviews: 0, Rejected: 1, PrevRejected: 0), d.Totals);
    }

    [Fact]
    public void Funnel_OverCollectedInPeriod_RejectionFromSent()
    {
        var d = DashboardMath.Build(Rows, 30, "hh", Now, Llm, Chats);

        Assert.Equal([("collected", 6), ("llm", 3), ("queued", 3), ("sent", 3), ("invited", 2), ("interview", 2), ("rejected", 1)],
            d.Funnel.Select(s => (s.Key, s.Count)));
        Assert.Equal("sent", d.Funnel.Single(s => s.Key == "rejected").Base);
        Assert.Equal("sent", d.Funnel.Single(s => s.Key == "interview").Base);
    }

    [Fact]
    public void BySearch_Reasons_AndSources()
    {
        var d = DashboardMath.Build(Rows, 30, "hh", Now, Llm, Chats);

        Assert.Equal([new DashboardSearchDto("backend", 3, 0, 0, 0, 0), new DashboardSearchDto("dotnet", 3, 3, 3, 2, 2)], d.BySearch);
        Assert.Equal([new DashboardReasonDto("оценка N ниже N", 2), new DashboardReasonDto("стоп-слово «…» в названии", 1)], d.Reasons);
        // Разрез по агрегаторам — без фильтра источника
        Assert.Equal(["hh", "hirify"], d.Sources.Select(s => s.Source));

        var all = DashboardMath.Build(Rows, 30, DashboardMath.AllSources, Now, Llm, Chats);
        Assert.Equal(7, all.Totals.Collected);
        Assert.Equal(0, DashboardMath.Build(Rows, 30, "getmatch", Now, Llm, Chats).Totals.Collected);
    }

    [Fact]
    public void Period7_CutsOlderEvents()
    {
        var d = DashboardMath.Build(Rows, 7, "hh", Now, Llm, Chats);

        // Собраны за 7 дней: 5, 2, 3, 3 дня назад; прошлые 7 дней — 10 и 12
        Assert.Equal((4, 2), (d.Totals.Collected, d.Totals.PrevCollected));
        Assert.Equal((1, 2), (d.Totals.Sent, d.Totals.PrevSent));
        Assert.Equal((2, 0), (d.Totals.Interviews, d.Totals.PrevInterviews));
        Assert.Equal(1, d.Totals.PrevRejected);
    }

    [Fact]
    public void Series_DaysByMoscow_WeeksFor90()
    {
        var days = DashboardMath.Build(Rows, 30, "hh", Now, Llm, Chats).Series;
        Assert.Equal(1, days.Bucket);
        Assert.Equal(30, days.Points.Count);
        // Последний столбец — сегодня с полуночи по Москве
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.FromHours(3)), days.Points[^1].At);
        Assert.Equal((3, 4, 3, 2), (days.Points.Sum(p => p.Sent), days.Points.Sum(p => p.Answered), days.Points.Sum(p => p.Invited),
            days.Points.Sum(p => p.Interviews)));

        var weeks = DashboardMath.Build(Rows, 90, "hh", Now, Llm, Chats).Series;
        Assert.Equal((7, 13), (weeks.Bucket, weeks.Points.Count));
        Assert.Equal(4, weeks.Points.Sum(p => p.Sent));
    }

    [Theory]
    [InlineData("оценка 59 ниже 70: мало опыта", "оценка N ниже N")]
    [InlineData("LLM: нужен Senior", "LLM")]
    [InlineData(null, "без причины")]
    [InlineData("", "без причины")]
    public void ReasonKey_GroupsNumbersAndQuotes(string? reason, string key)
    {
        Assert.Equal(key, DashboardMath.ReasonKey(reason));
    }

    // Корзины числа откликов (#148): нижняя граница включительно; отрицательный прирост — в первой корзине
    [Theory]
    [InlineData(0, "0–10")]
    [InlineData(9, "0–10")]
    [InlineData(10, "10–50")]
    [InlineData(49, "10–50")]
    [InlineData(50, "50–200")]
    [InlineData(199, "50–200")]
    [InlineData(200, "200+")]
    [InlineData(1536, "200+")]
    [InlineData(-3, "0–10")]
    [InlineData(null, DashboardMath.NoData)]
    public void ResponsesBucket_Bounds(int? count, string bucket)
    {
        Assert.Equal(bucket, DashboardMath.ResponsesBucket(count));
    }

    private static DashboardCompetitionRowDto R(string bucket, int sent, int invited, int rejected, int silent) =>
        new(bucket, sent, invited, rejected, silent);

    private static DashboardRow Sent(double appliedAgo, int? atCollect, int? atApply, VacancyStatus? answer = null)
    {
        var path = new List<(double, VacancyStatus)> { (appliedAgo, VacancyStatus.Applied) };
        if (answer is { } a)
            path.Add((appliedAgo - 0.5, a));
        return Row(appliedAgo + 1, path: [.. path]) with { ResponsesAtCollect = atCollect, ResponsesAtApply = atApply };
    }

    [Fact]
    public void Competition_OutcomesByResponses_AtCollectApplyAndGrowth()
    {
        DashboardRow[] rows =
        [
            Sent(3, 5, 8, VacancyStatus.Invited),
            Sent(4, 5, 30, VacancyStatus.Rejected),
            Sent(5, 120, 400),
            Sent(6, 300, 290, VacancyStatus.Interview),
            // До #148: чисел нет
            Sent(7, null, null, VacancyStatus.Rejected),
            // Только при сборе: расширение ещё старое
            Sent(8, 60, null),
            // Отправлена в прошлом периоде — не считается
            Sent(40, 5, 8, VacancyStatus.Invited),
            // Не отправлена — не считается
            Row(2, path: [(2, VacancyStatus.Queued)]) with { ResponsesAtCollect = 5 },
        ];

        var c = DashboardMath.Build(rows, 30, "hh", Now, Llm, Chats).Competition;

        Assert.Equal<DashboardCompetitionRowDto>([
            R("0–10", 2, 1, 1, 0), R("10–50", 0, 0, 0, 0), R("50–200", 2, 0, 0, 2), R("200+", 1, 1, 0, 0),
            R(DashboardMath.NoData, 1, 0, 1, 0),
        ], c.AtCollect);
        Assert.Equal<DashboardCompetitionRowDto>([
            R("0–10", 1, 1, 0, 0), R("10–50", 1, 0, 1, 0), R("50–200", 0, 0, 0, 0), R("200+", 2, 1, 0, 1),
            R(DashboardMath.NoData, 2, 0, 1, 1),
        ], c.AtApply);
        // Прирост: +3 и −10 → 0–10, +25 → 10–50, +280 → 200+; без одного из чисел — нет данных
        Assert.Equal<DashboardCompetitionRowDto>([
            R("0–10", 2, 2, 0, 0), R("10–50", 1, 0, 1, 0), R("50–200", 0, 0, 0, 0), R("200+", 1, 0, 0, 1),
            R(DashboardMath.NoData, 2, 0, 1, 1),
        ], c.Growth);
    }
}
