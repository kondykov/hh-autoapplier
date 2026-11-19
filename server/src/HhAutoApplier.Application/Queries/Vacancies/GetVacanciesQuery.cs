using System.ComponentModel.DataAnnotations;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Vacancies;
using OneOf;

namespace HhAutoApplier.Application.Queries.Vacancies;

public record GetVacanciesQuery
{
    public VacancyStatus? Status { get; init; }

    public string? Search { get; init; }

    // Текст (#141): каждое слово — подстрока названия или компании, без учёта регистра, «ё» = «е». Короче 2 символов
    // (без пробелов по краям) — пустой список; не больше 20 самых свежих. Для палитры Ctrl+K и перехода из неё в «Вакансии»
    [MaxLength(200)]
    public string? Q { get; init; }

    // Показать и скрытые карточки (#71); без него их в списке нет
    public bool IncludeHidden { get; init; }

    [Range(0, int.MaxValue)]
    public int Skip { get; init; }

    [Range(1, 500)]
    public int Take { get; init; } = 50;

    // Администратор — отклики этого пользователя; без него — свои
    public Guid? UserId { get; init; }
}

// На .NET 11: public union GetVacanciesResult(List<VacancyDto>, ForbiddenError);
[GenerateOneOf]
public partial class GetVacanciesResult : OneOfBase<List<VacancyDto>, ForbiddenError>;