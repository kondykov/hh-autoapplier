namespace HhAutoApplier.Domain.Llm;

// Задачи LLM, для которых сервер держит маршрут (#104), а пользователь может выбрать своё подключение (#107)
public static class LlmTasks
{
    public const string Evaluation = "Evaluation";

    public const string Letters = "Letters";

    public const string Questionnaires = "Questionnaires";

    public const string Dialogs = "Dialogs";

    public static readonly IReadOnlyList<string> All = [Evaluation, Letters, Questionnaires, Dialogs];

    // Имя задачи без учёта регистра → каноническое; неизвестное — null
    public static string? Find(string? name) => All.FirstOrDefault(t => string.Equals(t, name?.Trim(), StringComparison.OrdinalIgnoreCase));
}

// Выбор пользователя: задача → имя подключения сервера (Llm:Providers:<имя>)
public class LlmChoice
{
    public string Task { get; init; } = "";

    public string Connection { get; init; } = "";
}

// Агрегат: модели LLM пользователя по задачам (#107) — один на пользователя. Задачи без выбора идут маршрутом сервера по умолчанию.
// Есть ли подключение с таким именем, решает сценарий: список подключений — конфигурация сервера, а не домен
public class UserLlmSettings
{
    public const int MaxConnection = 64;

    // Для EF
    private UserLlmSettings()
    {
    }

    public int Id { get; private set; }

    // Владелец; null — владелец по умолчанию, пока нет пользователей
    public Guid? UserId { get; private set; }

    public List<LlmChoice> Choices { get; private set; } = [];

    public static UserLlmSettings Create(Guid? userId) => new() { UserId = userId };

    // Выбор целиком: задача без подключения (пусто) — маршрут сервера. Список пересобирается, чтобы EF видел изменение JSON-колонки
    public void Choose(IEnumerable<KeyValuePair<string, string?>> choices)
    {
        var next = new List<LlmChoice>();
        foreach (var (task, connection) in choices)
        {
            var known = LlmTasks.Find(task) ?? throw new DomainException($"Неизвестная задача LLM «{task}», допустимы {string.Join(", ", LlmTasks.All)}");
            if (next.Any(c => c.Task == known))
                throw new DomainException($"Задача {known} указана дважды");
            if (string.IsNullOrWhiteSpace(connection))
                continue;

            var name = connection.Trim();
            if (name.Length > MaxConnection)
                throw new DomainException($"Имя подключения длиннее {MaxConnection} символов");
            next.Add(new LlmChoice { Task = known, Connection = name });
        }

        Choices = next;
    }

    public string? ConnectionFor(string task) => Choices.FirstOrDefault(c => c.Task == task)?.Connection;
}
