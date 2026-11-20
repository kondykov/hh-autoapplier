using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Queries.LlmSettings;
using HhAutoApplier.Domain;
using HhAutoApplier.Domain.Llm;
using Microsoft.EntityFrameworkCore;
using OneOf;

namespace HhAutoApplier.Application.Commands.LlmSettings;

// Сохранить свои модели LLM целиком: задача → имя подключения сервера; пусто или задачи нет — как на сервере
public record SaveUserLlmSettingsCommand
{
    public Dictionary<string, string?> Choices { get; init; } = [];
}

// На .NET 11: public union SaveUserLlmSettingsResult(UserLlmSettingsDto, ValidationError);
[GenerateOneOf]
public partial class SaveUserLlmSettingsResult : OneOfBase<UserLlmSettingsDto, ValidationError>;

public class SaveUserLlmSettingsCommandHandler(IAppDbContext db, ILlmConnections connections, ICurrentUser current)
    : ICommandHandler<SaveUserLlmSettingsCommand, SaveUserLlmSettingsResult>
{
    public async Task<SaveUserLlmSettingsResult> Handle(SaveUserLlmSettingsCommand command, CancellationToken ct = default)
    {
        // Подключение, которого нет на сервере, не сохраняется: иначе задача молча шла бы маршрутом по умолчанию
        var unknown = command.Choices.Values.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && !connections.Exists(c.Trim()));
        if (unknown is not null)
            return new ValidationError($"Нет подключения LLM «{unknown.Trim()}»: выберите из списка подключений сервера");

        var settings = await db.UserLlmSettings.FirstOrDefaultAsync(s => s.UserId == current.Id, ct);
        if (settings is null)
        {
            settings = UserLlmSettings.Create(current.Id);
            db.UserLlmSettings.Add(settings);
        }

        try
        {
            settings.Choose(command.Choices);
        }
        catch (DomainException e)
        {
            return new ValidationError(e.Message);
        }

        await db.SaveChangesAsync(ct);
        return GetUserLlmSettingsQueryHandler.ToDto(settings);
    }
}
