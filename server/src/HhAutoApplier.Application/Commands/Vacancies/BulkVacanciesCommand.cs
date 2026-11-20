using System.ComponentModel.DataAnnotations;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Vacancies;

namespace HhAutoApplier.Application.Commands.Vacancies;

// Массовые действия над своими откликами (#127, «LLM → Отсев»): те же сценарии, что по одной вакансии, по списку id.
// Requeue — в очередь, Skip — обратно в отсев (причина LLM восстанавливается — отмена «Вернуть»), Reevaluate — оценить заново
// (Provider — разовым прогоном через подключение, #107), Hide / Unhide — скрыть и вернуть в список
public enum BulkVacancyAction
{
    Requeue = 0,
    Skip = 1,
    Reevaluate = 2,
    Hide = 3,
    Unhide = 4,
}

public record BulkVacanciesCommand
{
    public const int MaxIds = 500;

    [Required]
    public required BulkVacancyAction Action { get; init; }

    [Required, MinLength(1), MaxLength(MaxIds)]
    public required List<string> Ids { get; init; }

    [MaxLength(64)]
    public string? Provider { get; init; }
}

// Чужие id не находятся (NotFound в Failed): поиск — только среди откликов текущего пользователя
public class BulkVacanciesCommandHandler(
    IAppDbContext db,
    ICurrentUser current,
    SetVacancyStatusCommandHandler status,
    ReevaluateVacancyCommandHandler reevaluate,
    SetVacancyHiddenCommandHandler hidden
) : ICommandHandler<BulkVacanciesCommand, BulkVacanciesResultDto>
{
    public async Task<BulkVacanciesResultDto> Handle(BulkVacanciesCommand command, CancellationToken ct = default)
    {
        var done = new List<string>();
        var failed = new List<BulkFailureDto>();
        foreach (var id in command.Ids.Distinct())
        {
            var error = await Run(command, id, ct);
            if (error is null)
                done.Add(id);
            else
                failed.Add(new BulkFailureDto(id, error));
        }

        return new BulkVacanciesResultDto(done, failed);
    }

    private async Task<string?> Run(BulkVacanciesCommand command, string id, CancellationToken ct)
    {
        switch (command.Action)
        {
            case BulkVacancyAction.Requeue:
                return (await status.Handle(new SetVacancyStatusCommand { VacancyId = id, Status = VacancyStatus.Queued }, ct)).Match(_ => (string?)null, e => e.Message, e => e.Message);
            case BulkVacancyAction.Skip:
                var application = await db.FindApplicationAsync(current.Id, id, ct);
                var reason = application?.LlmRejectReason ?? "пропущено вручную";
                return (await status.Handle(new SetVacancyStatusCommand { VacancyId = id, Status = VacancyStatus.Skipped, Reason = reason }, ct))
                    .Match(_ => (string?)null, e => e.Message, e => e.Message);
            case BulkVacancyAction.Reevaluate:
                return (await reevaluate.Handle(new ReevaluateVacancyCommand { VacancyId = id, Provider = command.Provider }, ct))
                    .Match(_ => (string?)null, e => e.Message, e => e.Message, e => e.Message, e => e.Message);
            case BulkVacancyAction.Hide:
            case BulkVacancyAction.Unhide:
                return (await hidden.Handle(new SetVacancyHiddenCommand { VacancyId = id, Hidden = command.Action == BulkVacancyAction.Hide }, ct))
                    .Match(_ => (string?)null, e => e.Message, e => e.Message);
            default:
                return "неизвестное действие";
        }
    }
}
