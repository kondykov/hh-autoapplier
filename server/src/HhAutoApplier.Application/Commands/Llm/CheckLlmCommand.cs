using System.ComponentModel.DataAnnotations;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Commands.Llm;

// Проверить ключ и модель коротким запросом к провайдеру. Без параметров — основной провайдер; Connection — подключение
// для выбора (#107, Llm:Providers:<имя>), Task — маршрут задачи (#104: Evaluation, Letters, …) — для «LLM → Подключения» (#127)
public record CheckLlmCommand
{
    [MaxLength(64)]
    public string? Connection { get; init; }

    [MaxLength(32)]
    public string? Task { get; init; }
}

// На .NET 11: public union CheckLlmResult(LlmCheckDto, LlmUnavailableError, ValidationError);
[GenerateOneOf]
public partial class CheckLlmResult : OneOfBase<LlmCheckDto, LlmUnavailableError, ValidationError>;
