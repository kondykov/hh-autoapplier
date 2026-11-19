using HhAutoApplier.Application.Commands.LlmSettings;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.LlmSettings;
using Microsoft.AspNetCore.Mvc;

namespace HhAutoApplier.Api.Controllers;

// Выбор модели LLM пользователем (#107). Отдельно от LlmController: там операции только для администратора
[Tags("LLM: мои модели")]
[Route("api")]
public class LlmSettingsController : ApiController
{
    /// <summary>
    /// Подключения LLM сервера (Llm:Providers:имя) и чем сервер выполняет каждую задачу без выбора пользователя:
    /// провайдер, модель, доступность, пауза, расход за сутки (UTC) и лимит. Ключей и адресов нет. Любому вошедшему.
    /// </summary>
    [HttpGet("llm/providers")]
    [ProducesResponseType(typeof(LlmProvidersDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Providers([FromServices] GetLlmProvidersQueryHandler handler, CancellationToken ct)
    {
        return Ok(await handler.Handle(new GetLlmProvidersQuery(), ct));
    }

    /// <summary>
    /// Свои модели LLM по задачам (Evaluation, Letters, Questionnaires, Dialogs): имя подключения или null — как на сервере.
    /// Администратор — ?userId другого пользователя (только просмотр).
    /// </summary>
    [HttpGet("profile/llm")]
    [ProducesResponseType(typeof(UserLlmSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ForbiddenError), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(Guid? userId, [FromServices] GetUserLlmSettingsQueryHandler handler, CancellationToken ct)
    {
        return FromResult((await handler.Handle(new GetUserLlmSettingsQuery(userId), ct)).Value);
    }

    /// <summary>
    /// Сохранить свои модели LLM целиком. Задача без подключения — как на сервере; подключения нет на сервере — 400.
    /// </summary>
    [HttpPut("profile/llm")]
    [ProducesResponseType(typeof(UserLlmSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationError), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Save(
        [FromBody] SaveUserLlmSettingsCommand command,
        [FromServices] SaveUserLlmSettingsCommandHandler handler,
        CancellationToken ct
    )
    {
        return FromResult((await handler.Handle(command, ct)).Value);
    }
}
