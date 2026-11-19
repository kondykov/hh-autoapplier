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
}
