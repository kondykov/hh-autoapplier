using System.Diagnostics;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain.Llm;

namespace HhAutoApplier.Application.Commands.Llm;

public class CheckLlmCommandHandler(ILlmClient llm, IAppDbContext db, ILlmConnections connections, TimeProvider clock) : ICommandHandler<CheckLlmCommand, CheckLlmResult>
{
    private const string Purpose = "check";

    public async Task<CheckLlmResult> Handle(CheckLlmCommand command, CancellationToken ct = default)
    {
        // Неизвестное подключение клиент молча заменил бы маршрутом по умолчанию — проверка была бы не того
        if (command.Connection is { } name && !connections.Exists(name))
            return new ValidationError($"Подключения «{name}» нет на сервере");

        var task = command.Task is null ? null : LlmTasks.Find(command.Task);
        if (command.Task is not null && task is null)
            return new ValidationError($"Неизвестная задача LLM «{command.Task}», допустимы {string.Join(", ", LlmTasks.All)}");

        // Маршрут задачи выбирается по назначению запроса; в журнал вызов идёт как проверка
        var request = new LlmRequest([LlmMessage.User("Ответь одним словом: ok")], Temperature: 0, MaxTokens: 200,
            Purpose: task is null ? null : LlmRoutes.PurposesOf(task).First(), Provider: command.Connection);
        var prompt = request.Messages[0].Content;
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await llm.CompleteAsync(request, ct);
            // Модель ответила без текста (#81): запрос считается, но проверка не пройдена — пустой успех был бы ложным
            if (string.IsNullOrWhiteSpace(result.Content))
            {
                db.LlmCalls.Add(LlmCall.Rejected(Purpose, null, result.Model, prompt, result.Content, LlmCall.Unparsed, result.PromptTokens, result.CompletionTokens, clock.GetUtcNow()).Via(result.Connection));
                await db.SaveChangesAsync(ct);
                return new LlmUnavailableError("LLM недоступна: в ответе провайдера нет текста");
            }

            db.LlmCalls.Add(LlmCall.Succeeded(Purpose, null, result.Model, prompt, result.Content, result.PromptTokens, result.CompletionTokens, clock.GetUtcNow()).Via(result.Connection));
            await db.SaveChangesAsync(ct);
            return new LlmCheckDto(result.Model, result.Content.Trim(), (int)timer.ElapsedMilliseconds, result.PromptTokens + result.CompletionTokens);
        }
        catch (LlmUnavailableException e)
        {
            if (llm.IsConfigured)
            {
                db.LlmCalls.Add(LlmCall.Failed(Purpose, null, "", prompt, e.Message, clock.GetUtcNow()).Via(command.Connection));
                await db.SaveChangesAsync(ct);
            }
            return new LlmUnavailableError(e.Message);
        }
    }
}
