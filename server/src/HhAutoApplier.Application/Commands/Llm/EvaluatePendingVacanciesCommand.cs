using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Commands.Llm;

// LLM-оценка вакансий, прошедших правила (Pending): самые старые первыми, не больше Limit за запуск
public record EvaluatePendingVacanciesCommand
{
    [Range(1, 200)]
    public int Limit { get; init; } = 20;

    // Разовый прогон «Оценить заново через …» (#107): только этот отклик и только это подключение.
    // Не из тела запроса: через POST /api/llm/evaluate их не задать
    [JsonIgnore]
    public int? OnlyApplicationId { get; init; }

    [JsonIgnore]
    public string? Provider { get; init; }
}

// На .NET 11: public union EvaluatePendingResult(LlmEvaluationResultDto, LlmUnavailableError);
[GenerateOneOf]
public partial class EvaluatePendingResult : OneOfBase<LlmEvaluationResultDto, LlmUnavailableError>;
