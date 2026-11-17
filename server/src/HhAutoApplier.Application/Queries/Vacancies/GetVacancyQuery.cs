using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Queries.Vacancies;

// Свой отклик по id вакансии — карточка в Telegram-боте (#115)
public record GetVacancyQuery(string VacancyId);

// На .NET 11: public union GetVacancyResult(VacancyDto, NotFoundError);
[GenerateOneOf]
public partial class GetVacancyResult : OneOfBase<VacancyDto, NotFoundError>;
