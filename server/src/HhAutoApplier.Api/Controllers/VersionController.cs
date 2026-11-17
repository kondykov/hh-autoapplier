using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Queries.About;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HhAutoApplier.Api.Controllers;

[Tags("Версия")]
[Route("api/version")]
public class VersionController : ApiController
{
    /// <summary>
    /// Версия backend, коммит сборки и номер контракта API: по нему расширение проверяет совместимость.
    /// Без входа (#116): ничего, кроме того, что есть в публичном репозитории, и по нему deploy.sh сверяет выкатанный коммит.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(BuildVersionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromServices] GetVersionQueryHandler handler, CancellationToken ct)
    {
        return Ok(await handler.Handle(new GetVersionQuery(), ct));
    }
}
