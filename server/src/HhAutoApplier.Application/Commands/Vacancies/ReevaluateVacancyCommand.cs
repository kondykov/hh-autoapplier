using System.ComponentModel.DataAnnotations;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Commands.Vacancies;

// Вернуть отсеянный отклик на оценку (#71). Provider — разовый прогон (#107): оценить сразу этим подключением сервера
public record ReevaluateVacancyCommand
{
    [Required]
    public required string VacancyId { get; init; }

    public string? Provider { get; init; }
}

// Вакансия в архиве или недоступна (#147) — VacancyDto отклика в Skipped с причиной по вакансии; hh.ru не ответил — SourceUnavailableError
// На .NET 11: public union ReevaluateVacancyResult(VacancyDto, ValidationError, NotFoundError, ConflictError, LlmUnavailableError, SourceUnavailableError);
[GenerateOneOf]
public partial class ReevaluateVacancyResult : OneOfBase<VacancyDto, ValidationError, NotFoundError, ConflictError, LlmUnavailableError, SourceUnavailableError>;
