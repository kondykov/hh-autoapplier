using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Commands.Collecting;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Application.Commands.Vacancies;

// Перед переоценкой — не ушла ли вакансия в архив (#147): тем же источником и с теми же паузами, что у сбора.
// Экземпляр на запрос: массовая переоценка делает паузу между страницами и после блокировки больше не ходит к источнику
public class VacancyArchiveCheck(
    IAppDbContext db,
    IEnumerable<IVacancySource> sources,
    IOptions<CollectorOptions> options,
    TimeProvider clock
)
{
    private readonly CollectorOptions _opt = options.Value;

    private bool _requested;

    private string? _blocked;

    // Available, Archived или Unavailable; источник не ответил или блокирует — ошибка: переоценивать вслепую нельзя.
    // Отметку ArchiveCheckedAt ставит в вакансию, сохраняет вызывающий
    public async Task<(VacancyPageState State, SourceUnavailableError? Error)> CheckAsync(Vacancy vacancy, CancellationToken ct)
    {
        // Источников нет (Hh:Enabled = false) — проверить нечем, как и при сборе. Импортную карточку проверит подтверждение при сборе
        if (!sources.Any() || vacancy.Imported)
            return (VacancyPageState.Available, null);

        var due = clock.GetUtcNow() - CollectVacanciesCommandHandler.ArchiveCheckAfter;
        // Встречалась в выдаче недавно — в архиве её нет: выдача архивных не показывает (так же рассуждает сбор, #70)
        if (vacancy.LastSeenAt >= due)
            return (VacancyPageState.Available, null);

        // Проверена недавно: результат проверки не хранится в вакансии, его оставляют закрытые ею отклики
        if (vacancy.ArchiveCheckedAt >= due)
            return (await KnownStateAsync(vacancy.Id, ct), null);

        if (_blocked is not null)
            return (VacancyPageState.TemporaryError, new SourceUnavailableError(_blocked));

        var state = VacancyPageState.Unavailable;
        try
        {
            foreach (var source in sources)
            {
                if (_requested)
                    await PauseAsync(ct);

                _requested = true;
                var card = Vacancy.Create(vacancy.Id, "", "", vacancy.SearchName, clock.GetUtcNow());
                var found = await source.LoadDetailsAsync(card, ct);
                if (found is VacancyPageState.Available or VacancyPageState.Archived)
                {
                    state = found;
                    break;
                }

                if (found == VacancyPageState.TemporaryError)
                    state = found;
            }
        }
        catch (SourceBlockedException e)
        {
            _blocked = $"{e.SourceName}: {e.Reason} — проверить архив нельзя, переоценка не выполнена";
            return (VacancyPageState.TemporaryError, new SourceUnavailableError(_blocked));
        }

        if (state == VacancyPageState.TemporaryError)
            return (state, new SourceUnavailableError("Страница вакансии на hh.ru временно не отвечает — проверить архив нельзя, переоценка не выполнена"));

        vacancy.MarkArchiveChecked(clock.GetUtcNow());
        return (state, null);
    }

    private async Task<VacancyPageState> KnownStateAsync(string vacancyId, CancellationToken ct)
    {
        var reasons = await db.JobApplications.AsNoTracking()
            .Where(a => a.VacancyId == vacancyId && a.Status == VacancyStatus.Skipped
                && (a.StatusReason == JobApplication.VacancyArchived || a.StatusReason == JobApplication.VacancyUnavailable))
            .Select(a => a.StatusReason).ToListAsync(ct);
        return reasons.Contains(JobApplication.VacancyArchived) ? VacancyPageState.Archived
            : reasons.Count > 0 ? VacancyPageState.Unavailable
            : VacancyPageState.Available;
    }

    private Task PauseAsync(CancellationToken ct) =>
        Task.Delay(Random.Shared.Next(_opt.DelayMinMs, Math.Max(_opt.DelayMinMs, _opt.DelayMaxMs) + 1), ct);
}
