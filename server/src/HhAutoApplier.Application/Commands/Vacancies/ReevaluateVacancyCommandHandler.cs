using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Commands.Llm;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain;
using HhAutoApplier.Domain.Applications;
using HhAutoApplier.Domain.Services;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Vacancies;

public class ReevaluateVacancyCommandHandler(
    IAppDbContext db,
    IExtractor<JobApplication, VacancyDto> extractor,
    VacancyEvaluator evaluator,
    ILlmClient llm,
    ILlmConnections connections,
    EvaluatePendingVacanciesCommandHandler evaluate,
    TimeProvider clock,
    ICurrentUser current
) : ICommandHandler<ReevaluateVacancyCommand, ReevaluateVacancyResult>
{
    public async Task<ReevaluateVacancyResult> Handle(ReevaluateVacancyCommand command, CancellationToken ct = default)
    {
        var provider = string.IsNullOrWhiteSpace(command.Provider) ? null : command.Provider.Trim();
        // Разовый прогон неизвестным подключением молча шёл бы маршрутом по умолчанию — это не то, о чём просили
        if (provider is not null && !connections.Exists(provider))
            return new ValidationError($"Нет подключения LLM «{provider}»: выберите из списка подключений сервера");

        var application = await db.FindApplicationAsync(current.Id, command.VacancyId, ct);
        if (application is null)
            return new NotFoundError($"Вакансия {command.VacancyId} не найдена");

        // В том числе для импортной карточки: без LLM после подтверждения она ушла бы в очередь мимо оценки
        if (!llm.IsConfigured)
            return new LlmUnavailableError("LLM не настроена: оценить заново нельзя, задайте LLM_API_KEY");

        var resumes = await db.Resumes.AsNoTracking().Where(r => r.UserId == current.Id).ToListAsync(ct);
        try
        {
            application.Reevaluate(evaluator, application.Rules, resumes, clock.GetUtcNow());
        }
        catch (DomainException e)
        {
            return new ConflictError(e.Message);
        }

        await db.SaveChangesAsync(ct);

        // Прошла правила — оценка сразу выбранным подключением (#107). Провайдер не ответил или доля исчерпана — отклик остаётся
        // Pending, его оценит конвейер обычным выбором; письмо после оценки тоже пишет конвейер
        if (provider is not null && application.Status == VacancyStatus.Pending)
            await evaluate.Handle(new EvaluatePendingVacanciesCommand { Limit = 1, OnlyApplicationId = application.Id, Provider = provider }, ct);

        return extractor.Extract(application);
    }
}
