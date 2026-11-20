using HhAutoApplier.Application.Commands.Llm;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.Llm;
using Microsoft.AspNetCore.Mvc;

namespace HhAutoApplier.Api.Controllers;

// Вкладка LLM в вебе (#127): любому вошедшему по своим данным; администратор — ?userId другого пользователя
[Tags("LLM: вкладка")]
[Route("api/llm")]
public class LlmTabController : ApiController
{
    /// <summary>
    /// Обзор: запросы за сутки и лимит (по пользователям — только администратору), ошибки за 24 ч по видам, отсеяно LLM за 30 дней
    /// и возвращено вручную, письма, отклонённые проверкой, маршруты задач с доступностью и «требует внимания».
    /// Администратор без ?userId видит запросы и ошибки всего сервера.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(LlmOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ForbiddenError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Overview(Guid? userId, [FromServices] GetLlmOverviewQueryHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(new GetLlmOverviewQuery(userId), ct)).Value);
    }

    /// <summary>
    /// Отсеянные LLM (не скрытые): группы по ключевым словам причины со счётчиками и страница списка (балл по убыванию).
    /// period — дней (7, 30), без него — всё; group — id группы; border=true — только пограничные (балл 60–69).
    /// Массовые действия — POST /api/vacancies/bulk.
    /// </summary>
    [HttpGet("rejects")]
    [ProducesResponseType(typeof(LlmRejectsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ForbiddenError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Rejects([FromQuery] GetLlmRejectsQuery query, [FromServices] GetLlmRejectsQueryHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(query, ct)).Value);
    }

    /// <summary>
    /// Журнал вызовов LLM от новых к старым: время, задача, модель, итог (ok, rejected — ответ не принят, error — ответа не было), токены;
    /// одинаковые ошибки подряд свёрнуты в строку с count и since. Пользователь — свои вызовы; администратор без ?userId — все, с пользователем.
    /// purpose — назначение, model — модель, errors=true — только ошибки. Промпт и ответ — GET /api/llm/calls/{id}.
    /// </summary>
    [HttpGet("calls")]
    [ProducesResponseType(typeof(LlmCallsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ForbiddenError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Calls([FromQuery] GetLlmCallsQuery query, [FromServices] GetLlmCallsQueryHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(query, ct)).Value);
    }

    /// <summary>
    /// Вызов LLM целиком: промпт и ответ — как есть, показывать только текстом. Свой — любому; чужой — только администратору, иначе 404.
    /// </summary>
    [HttpGet("calls/{id:long}")]
    [ProducesResponseType(typeof(LlmCallDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundError), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Call(long id, [FromServices] GetLlmCallQueryHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(new GetLlmCallQuery(id), ct)).Value);
    }

    /// <summary>
    /// Письма LLM, отклонённые проверкой (отклики на проверке с причиной «письмо: проверить»): вакансия, причина, черновик из журнала
    /// и его модель; rewritten — письмо уже переписано и ждёт одобрения в «Проверке».
    /// </summary>
    [HttpGet("letters")]
    [ProducesResponseType(typeof(List<LlmLetterDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ForbiddenError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Letters(Guid? userId, [FromServices] GetLlmLettersQueryHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(new GetLlmLettersQuery(userId), ct)).Value);
    }

    /// <summary>
    /// Написать заново своё письмо, отклонённое проверкой: LLM пишет сразу (один-два запроса дневного лимита), без суточной паузы.
    /// Отклик остаётся на проверке — новое письмо одобряется в «Проверке»; не прошло проверку снова — новая причина.
    /// </summary>
    /// <response code="404">Своего отклика с отклонённым письмом нет.</response>
    /// <response code="409">Письмо уже поправлено вручную.</response>
    /// <response code="503">LLM не настроена.</response>
    [HttpPost("letters/{vacancyId}/rewrite")]
    [ProducesResponseType(typeof(CoverLettersResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotFoundError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ConflictError), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(LlmUnavailableError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RewriteLetter(string vacancyId, [FromServices] RewriteCoverLetterCommandHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(new RewriteCoverLetterCommand(vacancyId), ct)).Value);
    }
}
