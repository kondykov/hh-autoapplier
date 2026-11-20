using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using OneOf;

namespace HhAutoApplier.Application.Commands.Llm;

// «LLM → Письма → Написать заново» (#127): своё письмо, отклонённое проверкой, LLM пишет сразу, без суточной паузы после
// двух неудач. Отклик остаётся на проверке: новое письмо одобряет человек в «Проверке»
public record RewriteCoverLetterCommand(string VacancyId);

// На .NET 11: public union RewriteCoverLetterResult(CoverLettersResultDto, NotFoundError, ConflictError, LlmUnavailableError);
[GenerateOneOf]
public partial class RewriteCoverLetterResult : OneOfBase<CoverLettersResultDto, NotFoundError, ConflictError, LlmUnavailableError>;
