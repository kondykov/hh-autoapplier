using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Queries.Links;
using HhAutoApplier.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HhAutoApplier.Api.Controllers;

[Tags("Состояние")]
[Route("api/health")]
// Адрес прокси и журнал сбоев — для администратора; деплой проверяет открытый /health
[Authorize(Roles = Roles.Admin)]
public class HealthController : ApiController
{
    /// <summary>
    /// Связь по направлениям (#132): Telegram и провайдеры LLM — через прокси или напрямую, сам прокси; по последней проверке
    /// (раз в 2 минуты, при сбое — раз в 30 с) — состояние и последний успех; открытые и последние сбои; сколько уведомлений ждёт связи.
    /// </summary>
    [HttpGet("links")]
    [ProducesResponseType(typeof(LinksDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Links([FromServices] GetLinksQueryHandler handler, CancellationToken ct)
    {
        return Ok(await handler.Handle(new GetLinksQuery(), ct));
    }
}
