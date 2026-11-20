using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Queries.Llm;

// «LLM → Обзор» (#127): свои данные; администратор — пользователя UserId, без него запросы и ошибки — по всему серверу
public record GetLlmOverviewQuery(Guid? UserId = null);

// На .NET 11: public union GetLlmOverviewResult(LlmOverviewDto, ForbiddenError);
[GenerateOneOf]
public partial class GetLlmOverviewResult : OneOfBase<LlmOverviewDto, ForbiddenError>;
