using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.Dashboard;
using HhAutoApplier.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HhAutoApplier.Api.Controllers;

// Дашборд веба (#121): метрики профиля — любому вошедшему по своим данным, система — только администратору
[Tags("Дашборд")]
[Route("api/dashboard")]
public class DashboardController : ApiController
{
    /// <summary>
    /// Метрики профиля за period (7, 30, 90 дней): ключевые числа с прошлым периодом, воронка по собранным за период
    /// (собрано → оценено LLM → в очередь → отправлено → приглашение → собеседование; отказ — от отправленных),
    /// отклики по дням (за 90 — по неделям, дни по Москве), по поискам, причины отсева, разрез по агрегаторам, LLM за сутки
    /// и когда пришло последнее сообщение из чатов. source — агрегатор (hh), all — все. Администратор — ?userId другого пользователя.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(DashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ForbiddenError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get([FromQuery] GetDashboardQuery query, [FromServices] GetDashboardQueryHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(query, ct)).Value);
    }

    /// <summary>
    /// Система: версия и коммит, сбор (идущий и последний проход), LLM сервера (как обзор вкладки LLM без ?userId),
    /// пользователи за period (собрано, отправлено, расход LLM, последняя отправка и сообщение из чатов), неотправленные уведомления.
    /// </summary>
    [HttpGet("system")]
    [Authorize(Roles = Roles.Admin)]
    [ProducesResponseType(typeof(SystemDashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ForbiddenError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> System([FromQuery] GetSystemDashboardQuery query, [FromServices] GetSystemDashboardQueryHandler handler,
        CancellationToken ct)
    {
        return FromResult((await handler.Handle(query, ct)).Value);
    }
}
