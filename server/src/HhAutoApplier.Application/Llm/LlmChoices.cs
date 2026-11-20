using HhAutoApplier.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Llm;

// Выбор подключений владельцами откликов на один проход конвейера (#107): задача → имя подключения или null (маршрут сервера)
public sealed class LlmChoices
{
    private readonly Dictionary<(Guid?, string), string> _choices = [];

    // Разовый прогон (#107): одно подключение на все задачи прохода
    private string? _only;

    private LlmChoices()
    {
    }

    public static LlmChoices Only(string connection) => new() { _only = connection };

    public static async Task<LlmChoices> LoadAsync(IAppDbContext db, IEnumerable<Guid?> owners, CancellationToken ct)
    {
        var ids = owners.Distinct().ToList();
        var result = new LlmChoices();
        if (ids.Count == 0)
            return result;

        var settings = await db.UserLlmSettings.AsNoTracking().Where(s => ids.Contains(s.UserId)).ToListAsync(ct);
        foreach (var s in settings)
        foreach (var choice in s.Choices)
            result._choices[(s.UserId, choice.Task)] = choice.Connection;
        return result;
    }

    // Задача назначения запроса (LlmRoutes.Of): разбор просьб к письму — та же задача, что оценка
    public string? For(Guid? owner, string purpose) =>
        _only ?? (LlmRoutes.Of(purpose) is { } task && _choices.TryGetValue((owner, task), out var connection) ? connection : null);
}
