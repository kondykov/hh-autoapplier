using System.Text.RegularExpressions;
using HhAutoApplier.Application.Abstractions;

namespace HhAutoApplier.Application.Llm;

// Именованные подключения LLM (#107): Llm:Providers:<имя>, поля — как у маршрута (#104). Пользователь выбирает их по задачам
public static partial class LlmConnections
{
    // Имя видно пользователю и хранится в его настройках: латиница, цифры, «-», «_», «.»
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();

    // Действующие подключения с итоговыми настройками; ошибка конфигурации — при старте
    public static IReadOnlyDictionary<string, LlmOptions> Resolve(LlmOptions main)
    {
        var resolved = new Dictionary<string, LlmOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, connection) in main.Providers)
        {
            if (!NamePattern().IsMatch(name))
                throw new InvalidOperationException($"Llm:Providers: имя подключения «{name}» — латиница, цифры, «-», «_», «.», до 64 символов");
            if (!connection.IsSet)
                continue;
            if (connection.ChangesProvider && string.IsNullOrWhiteSpace(connection.ApiKey))
                throw new InvalidOperationException($"Llm:Providers:{name}: другой провайдер или адрес — задайте и свой ключ (ApiKey): ключ основного провайдера ему не передаётся");

            var options = connection.Over(main);
            try
            {
                options.EnsureValid();
            }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Llm:Providers:{name}: {e.Message}", e);
            }

            resolved[name] = options;
        }

        return resolved;
    }
}
