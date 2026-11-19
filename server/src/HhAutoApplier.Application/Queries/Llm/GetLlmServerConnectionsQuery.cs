using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Links;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain.Llm;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Application.Queries.Llm;

// «LLM → Подключения» (#127, только администратору): основной провайдер, маршруты задач (#104) и подключения для выбора (#107)
// с расходом, паузой, адресом провайдера без пути и маской ключа; маршруты по умолчанию «задача → подключение». Учётных данных нет
public record GetLlmServerConnectionsQuery;

public class GetLlmServerConnectionsQueryHandler(ILlmConnections connections, IOptions<LlmOptions> options, LinkStatus links)
    : IQueryHandler<GetLlmServerConnectionsQuery, LlmServerConnectionsDto>
{
    public Task<LlmServerConnectionsDto> Handle(GetLlmServerConnectionsQuery query, CancellationToken ct = default)
    {
        var main = options.Value;
        var routes = Safe(() => LlmRoutes.Resolve(main));
        var named = Safe(() => LlmConnections.Resolve(main));

        // Основной и маршруты — те, что сервер действительно использует по умолчанию; подключения для выбора — все
        var defaults = LlmTasks.All.Select(t => new LlmTaskDefaultDto(t, ToDto(connections.DefaultFor(t)))).ToList();
        var list = new List<LlmServerConnectionDto>();
        foreach (var d in defaults.Select(d => d.Connection).DistinctBy(c => c.Name))
        {
            var route = routes.FirstOrDefault(r => string.Equals(r.Key, d.Name, StringComparison.OrdinalIgnoreCase)).Value;
            list.Add(new LlmServerConnectionDto(route is null ? "main" : "route", d, Host((route ?? main).BaseUrl), LlmKeyMask.Of((route ?? main).ApiKey)));
        }

        foreach (var s in connections.Named())
        {
            var o = named.FirstOrDefault(n => string.Equals(n.Key, s.Name, StringComparison.OrdinalIgnoreCase)).Value;
            list.Add(new LlmServerConnectionDto("named", ToDto(s), o is null ? null : Host(o.BaseUrl), LlmKeyMask.Of(o?.ApiKey)));
        }

        return Task.FromResult(new LlmServerConnectionsDto(list, defaults, links.Proxy));
    }

    // Конфигурацию уже проверил старт; здесь ошибка не должна ронять страницу
    private static IReadOnlyDictionary<string, LlmOptions> Safe(Func<IReadOnlyDictionary<string, LlmOptions>> resolve)
    {
        try
        {
            return resolve();
        }
        catch (InvalidOperationException)
        {
            return new Dictionary<string, LlmOptions>();
        }
    }

    // Только схема и хост: путь и параметры адреса могут нести ключ
    public static string? Host(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}" : null;

    private static LlmConnectionDto ToDto(LlmConnectionStatus s) =>
        new(s.Name, s.Provider, s.Model, s.Available, s.PausedUntil, s.PauseReason, s.UsedToday, s.DailyLimit);
}

// Ключ в вебе (#127): последние 4 символа, у короткого — только «задан»; null — не задан
public static class LlmKeyMask
{
    public const string Set = "задан";

    private const int Shown = 4;

    // Короче этого последние символы раскрывали бы заметную часть ключа
    private const int MinLength = 16;

    public static string? Of(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        key = key.Trim();
        return key.Length < MinLength ? Set : $"…{key[^Shown..]}";
    }
}
