using System.ComponentModel.DataAnnotations;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Vacancies;
using OneOf;

namespace HhAutoApplier.Application.Commands.Vacancies;

// Клиент сообщает исход: откликнулся, пропустил, ошибка, отказ работодателя, вернуть в очередь
public record SetVacancyStatusCommand
{
    [Required]
    public required string VacancyId { get; init; }

    [Required]
    public required VacancyStatus Status { get; init; }

    public string? Reason { get; init; }

    public string? Resume { get; init; }

    // Число откликов на вакансию перед своим (#148): учитывается только с Applied, отрицательное — как нет данных
    public int? Responses { get; init; }
}

// На .NET 11: public union SetVacancyStatusResult(VacancyDto, NotFoundError, ConflictError);
[GenerateOneOf]
public partial class SetVacancyStatusResult : OneOfBase<VacancyDto, NotFoundError, ConflictError>;
