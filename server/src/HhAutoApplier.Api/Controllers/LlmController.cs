using HhAutoApplier.Application.Commands.Llm;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.Llm;
using HhAutoApplier.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HhAutoApplier.Api.Controllers;

[Tags("LLM")]
[Route("api/llm")]
// Общие операции над данными всех пользователей — только Admin
[Authorize(Roles = Roles.Admin)]
public class LlmController : ApiController
{
    /// <summary>
    /// Проверить ключ и модель LLM коротким запросом к провайдеру (расходует один запрос дневного лимита).
    /// Без параметров — основной провайдер; ?connection — подключение для выбора (#107), ?task — маршрут задачи (#104).
    /// </summary>
    /// <response code="400">Подключения или задачи нет на сервере.</response>
    /// <response code="503">LLM не настроена, исчерпан дневной лимит или провайдер не отвечает.</response>
    [HttpPost("check")]
    [ProducesResponseType(typeof(LlmCheckDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(LlmUnavailableError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Check([FromQuery] CheckLlmCommand command, [FromServices] CheckLlmCommandHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(command, ct)).Value);
    }

    /// <summary>
    /// Подключения LLM сервера для «LLM → Подключения» (#127): основной провайдер, маршруты задач и подключения для выбора —
    /// провайдер, модель, адрес без пути, ключ маской (последние 4 символа или «задан»), расход, лимит, пауза; маршруты по умолчанию
    /// и общий прокси без учётных данных. Состояние связи — GET /api/health/links.
    /// </summary>
    [HttpGet("connections")]
    [ProducesResponseType(typeof(LlmServerConnectionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Connections([FromServices] GetLlmServerConnectionsQueryHandler handler, CancellationToken ct)
    {
        return Ok(await handler.Handle(new GetLlmServerConnectionsQuery(), ct));
    }

    /// <summary>
    /// Оценить LLM вакансии, прошедшие правила (Pending): подходит — в очередь, сомнительно — в очередь с пометкой, не подходит — Skipped.
    /// Каждая вакансия — один запрос дневного лимита; при исчерпании оставшиеся ждут следующего запуска.
    /// </summary>
    /// <response code="503">LLM не настроена.</response>
    [HttpPost("evaluate")]
    [ProducesResponseType(typeof(LlmEvaluationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LlmUnavailableError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Evaluate(
        [FromBody] EvaluatePendingVacanciesCommand command,
        [FromServices] EvaluatePendingVacanciesCommandHandler handler,
        CancellationToken ct)
    {
        return FromResult((await handler.Handle(command, ct)).Value);
    }

    /// <summary>
    /// Ответить LLM на анкеты работодателей (Questionnaire), которые прислало расширение: с ответами — в очередь,
    /// неполный ответ или вариант не из анкеты — Skipped с причиной. Каждая вакансия — один запрос дневного лимита.
    /// </summary>
    /// <response code="503">LLM не настроена.</response>
    [HttpPost("answers")]
    [ProducesResponseType(typeof(QuestionnaireAnswersResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LlmUnavailableError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Answers(
        [FromBody] AnswerQuestionnairesCommand command,
        [FromServices] AnswerQuestionnairesCommandHandler handler,
        CancellationToken ct)
    {
        return FromResult((await handler.Handle(command, ct)).Value);
    }

    /// <summary>
    /// Написать LLM сопроводительные письма для вакансий в очереди без письма: по резюме, выбранному для вакансии, и его шаблону письма.
    /// Письмо уходит в расширение вместе с очередью; каждая вакансия — один запрос дневного лимита.
    /// </summary>
    /// <response code="503">LLM не настроена.</response>
    [HttpPost("letters")]
    [ProducesResponseType(typeof(CoverLettersResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LlmUnavailableError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Letters(
        [FromBody] WriteCoverLettersCommand command,
        [FromServices] WriteCoverLettersCommandHandler handler,
        CancellationToken ct)
    {
        return FromResult((await handler.Handle(command, ct)).Value);
    }

    /// <summary>
    /// Сбросить письма вакансий в очереди (все или по списку id), чтобы LLM написала их заново — после правки резюме, шаблона или промпта.
    /// Новые письма пишет POST /api/llm/letters или фоновый запуск; до этого расширение вставит свой шаблон. LLM не вызывает.
    /// Письма администратора; ?userId — другого пользователя. Поправленные человеком — только с includeManual: true.
    /// </summary>
    [HttpPost("letters/reset")]
    [ProducesResponseType(typeof(ResetCoverLettersResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetLetters(
        [FromBody] ResetCoverLettersCommand command,
        [FromQuery] Guid? userId,
        [FromServices] ResetCoverLettersCommandHandler handler,
        CancellationToken ct)
    {
        return Ok(await handler.Handle(command with { UserId = userId }, ct));
    }
}
