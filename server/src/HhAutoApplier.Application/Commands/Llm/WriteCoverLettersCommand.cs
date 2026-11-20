using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Commands.Llm;

// Письма для вакансий в очереди и на проверке, у которых письма ещё нет: лучшие по баллу первыми, не больше Limit за запуск
public record WriteCoverLettersCommand
{
    [Range(1, 200)]
    public int Limit { get; init; } = 20;

    // Одно письмо (#127, «Написать заново»): только эта вакансия этого владельца и без суточной паузы после двух неудач.
    // Задаёт сценарий RewriteCoverLetterCommand, не тело запроса
    [JsonIgnore]
    public string? VacancyId { get; init; }

    [JsonIgnore]
    public Guid? UserId { get; init; }
}

// На .NET 11: public union WriteCoverLettersResult(CoverLettersResultDto, LlmUnavailableError);
[GenerateOneOf]
public partial class WriteCoverLettersResult : OneOfBase<CoverLettersResultDto, LlmUnavailableError>;
