using System.Text.RegularExpressions;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;

namespace HhAutoApplier.Application.Queries.Dashboard;

// Отклик для дашборда: когда собран, история статусов и что нужно для разрезов. Source — агрегатор (пока у всех hh)
public record DashboardRow(
    DateTimeOffset CreatedAt,
    VacancyStatus Status,
    string? StatusReason,
    int? LlmScore,
    DateTimeOffset? AppliedAt,
    string Search,
    string Source,
    IReadOnlyList<ApplicationEvent> History,
    int? ResponsesAtCollect = null,
    int? ResponsesAtApply = null);

// Расчёт дашборда по откликам без базы — проверяется тестами на наборе данных
public static partial class DashboardMath
{
    public static readonly int[] Periods = [7, 30, 90];

    public const string AllSources = "all";

    public const int TopReasons = 6;

    // Дни — по Москве: столбец «сегодня» начинается в полночь МСК
    public static readonly TimeSpan Moscow = TimeSpan.FromHours(3);

    // Когда отклик впервые пришёл в статус; null — не приходил. Отправка — ещё и по AppliedAt (восстановленные по чату)
    public static DateTimeOffset? Reached(DashboardRow row, VacancyStatus status)
    {
        var at = row.History.FirstOrDefault(e => e.To == status)?.At;
        return at ?? (status == VacancyStatus.Applied ? row.AppliedAt : null);
    }

    // Приглашение в широком смысле: приглашение или сразу собеседование — что раньше
    public static DateTimeOffset? Invited(DashboardRow row) => Min(Reached(row, VacancyStatus.Invited), Reached(row, VacancyStatus.Interview));

    // Ответ работодателя: приглашение, собеседование или отказ — первый
    public static DateTimeOffset? Answered(DashboardRow row) => Min(Invited(row), Reached(row, VacancyStatus.Rejected));

    public static DashboardDto Build(IReadOnlyList<DashboardRow> all, int period, string source, DateTimeOffset now,
        DashboardLlmDto llm, DashboardChatsDto chats)
    {
        var from = now.AddDays(-period);
        var prevFrom = from.AddDays(-period);
        var rows = source == AllSources ? all : all.Where(r => r.Source == source).ToList();
        bool In(DateTimeOffset? at, DateTimeOffset a, DateTimeOffset b) => at is { } t && t >= a && t < b;
        int Count(Func<DashboardRow, DateTimeOffset?> when, DateTimeOffset a, DateTimeOffset b) => rows.Count(r => In(when(r), a, b));

        var cohort = rows.Where(r => In(r.CreatedAt, from, now)).ToList();
        Func<DashboardRow, DateTimeOffset?> collected = r => r.CreatedAt, sent = r => Reached(r, VacancyStatus.Applied),
            interview = r => Reached(r, VacancyStatus.Interview), rejected = r => Reached(r, VacancyStatus.Rejected);
        var totals = new DashboardTotalsDto(
            Count(collected, from, now), Count(collected, prevFrom, from),
            Count(sent, from, now), Count(sent, prevFrom, from),
            Count(Invited, from, now), Count(Invited, prevFrom, from),
            Count(interview, from, now), Count(interview, prevFrom, from),
            Count(rejected, from, now), Count(rejected, prevFrom, from));

        // Воронка: % — от предыдущего шага, отказ — от отправленных
        int Step(Func<DashboardRow, bool> passed) => cohort.Count(passed);
        IReadOnlyList<DashboardFunnelStepDto> funnel =
        [
            new("collected", "Собрано", cohort.Count, null),
            new("llm", "Оценено LLM", Step(r => r.LlmScore is not null), "collected"),
            new("queued", "В очередь", Step(r => Reached(r, VacancyStatus.Queued) is not null || sent(r) is not null), "llm"),
            new("sent", "Отправлено", Step(r => sent(r) is not null), "queued"),
            new("invited", "Приглашение", Step(r => Invited(r) is not null), "sent"),
            new("interview", "Собеседование", Step(r => interview(r) is not null), "sent"),
            new("rejected", "Отказ", Step(r => rejected(r) is not null), "sent"),
        ];

        var bySearch = cohort.GroupBy(r => r.Search)
            .Select(g => new DashboardSearchDto(g.Key, g.Count(), g.Count(r => sent(r) is not null), g.Count(r => Answered(r) is not null),
                g.Count(r => Invited(r) is not null), g.Count(r => interview(r) is not null)))
            .OrderByDescending(s => s.Collected).ThenBy(s => s.Name).ToList();

        var reasons = cohort.Where(r => r.Status == VacancyStatus.Skipped).GroupBy(r => ReasonKey(r.StatusReason))
            .Select(g => new DashboardReasonDto(g.Key, g.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Reason).Take(TopReasons).ToList();

        // Разрез по агрегаторам — по всем откликам, без фильтра источника: переключатель показывает, где что есть
        var sources = all.Where(r => In(r.CreatedAt, from, now)).GroupBy(r => r.Source)
            .Select(g => new DashboardSourceDto(g.Key, g.Count(), g.Count(r => sent(r) is not null), g.Count(r => Invited(r) is not null),
                g.Count(r => interview(r) is not null)))
            .OrderByDescending(s => s.Collected).ToList();

        var sentInPeriod = rows.Where(r => In(sent(r), from, now)).ToList();
        return new DashboardDto(period, source, from, now, totals, funnel, Series(rows, period, now), bySearch, reasons, sources, llm, chats,
            Competition(sentInPeriod));
    }

    // Корзины числа откликов (#148): нижняя граница включительно, верхняя — нет
    public static readonly int[] ResponseBuckets = [0, 10, 50, 200];

    public const string NoData = "нет данных";

    public static string ResponsesBucket(int? count)
    {
        if (count is not { } n)
            return NoData;

        // Прирост бывает отрицательным: поля hh.ru из разных кэшей расходятся на единицы — это «почти не выросло»
        var i = Array.FindLastIndex(ResponseBuckets, b => Math.Max(n, 0) >= b);
        return i == ResponseBuckets.Length - 1 ? $"{ResponseBuckets[i]}+" : $"{ResponseBuckets[i]}–{ResponseBuckets[i + 1]}";
    }

    // Прирост между сбором и своим откликом; нет одного из чисел — нет данных
    public static int? ResponsesGrowth(DashboardRow row) =>
        row is { ResponsesAtCollect: { } collected, ResponsesAtApply: { } applied } ? applied - collected : null;

    // Исходы отправленных: приглашение (или сразу собеседование), иначе отказ, иначе молчание. Все корзины — всегда, «нет данных» — последней
    public static DashboardCompetitionDto Competition(IReadOnlyList<DashboardRow> sent)
    {
        IReadOnlyList<DashboardCompetitionRowDto> Slice(Func<DashboardRow, int?> count)
        {
            var labels = ResponseBuckets.Select(b => ResponsesBucket(b)).Append(NoData);
            var groups = sent.ToLookup(r => ResponsesBucket(count(r)));
            return labels.Select(label =>
            {
                var g = groups[label].ToList();
                var invited = g.Count(r => Invited(r) is not null);
                var rejected = g.Count(r => Invited(r) is null && Reached(r, VacancyStatus.Rejected) is not null);
                return new DashboardCompetitionRowDto(label, g.Count, invited, rejected, g.Count - invited - rejected);
            }).ToList();
        }

        return new DashboardCompetitionDto(Slice(r => r.ResponsesAtCollect), Slice(r => r.ResponsesAtApply), Slice(ResponsesGrowth));
    }

    // По дням, за 90 дней — по неделям; последний столбец кончается сегодня
    public static DashboardSeriesDto Series(IReadOnlyList<DashboardRow> rows, int period, DateTimeOffset now)
    {
        var bucket = period > 30 ? 7 : 1;
        var buckets = (period + bucket - 1) / bucket;
        var local = now.ToOffset(Moscow);
        var today = new DateTimeOffset(local.Date, Moscow);
        var first = today.AddDays(-(buckets * bucket - 1));
        var points = Enumerable.Range(0, buckets).Select(i => new Point(first.AddDays(i * bucket))).ToArray();

        void Add(DateTimeOffset? at, Action<Point> inc)
        {
            if (at is not { } t || t < first || t >= now)
                return;

            var i = (int)((t - first).TotalDays / bucket);
            if (i < points.Length)
                inc(points[i]);
        }

        foreach (var row in rows)
        {
            Add(Reached(row, VacancyStatus.Applied), p => p.Sent++);
            Add(Answered(row), p => p.Answered++);
            Add(Invited(row), p => p.Invited++);
            Add(Reached(row, VacancyStatus.Interview), p => p.Interviews++);
        }

        return new DashboardSeriesDto(bucket, points.Select(p => new DashboardPointDto(p.At, p.Sent, p.Answered, p.Invited, p.Interviews)).ToList());
    }

    // Причина отсева — по первой части («правило: …», «LLM: …»); числа и слова в кавычках — общим местом,
    // чтобы «оценка 59 ниже 70» и «оценка 62 ниже 70» легли в одну строку
    public static string ReasonKey(string? reason)
    {
        var head = Separators().Split(reason ?? "")[0];
        var key = Digits().Replace(Quoted().Replace(head, "«…»"), "N").Trim();
        if (key.Length > 70)
            key = key[..70].TrimEnd();
        return key.Length == 0 ? NoReason : key;
    }

    public const string NoReason = "без причины";

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private sealed class Point(DateTimeOffset at)
    {
        public DateTimeOffset At { get; } = at;

        public int Sent { get; set; }

        public int Answered { get; set; }

        public int Invited { get; set; }

        public int Interviews { get; set; }
    }

    [GeneratedRegex("[:.(]")]
    private static partial Regex Separators();

    [GeneratedRegex("«[^»]*»")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
