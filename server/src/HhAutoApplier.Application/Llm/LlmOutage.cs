namespace HhAutoApplier.Application.Llm;

// Перерыв в работе LLM для уведомлений администратору (#108): сообщение при смене состояния, а не одно в сутки.
// Раньше утреннее «приостановлены до …» после включения сервера оставалось последним словом, хотя через четверть часа
// LLM отвечала. Восстановление — после проходов без сбоев RecoveryWindow подряд: краткие перебои (пауза 5 минут после
// сетевой ошибки, #68) не дают пары сообщений каждые пять минут. Состояние в памяти: после перезапуска сбой сообщается заново
public class LlmOutage(TimeProvider clock)
{
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();

    // Начало перерыва; null — LLM отвечает
    private DateTimeOffset? _since;

    // Первый проход без сбоев в перерыве
    private DateTimeOffset? _cleanSince;

    // Итог прохода конвейера. reason — почему проход остановился, null — прошёл всё; worked — были ли запросы.
    // Проход без работы ничего не говорит о LLM, но досчитывает окно восстановления, если чистый проход уже был
    public LlmOutageChange? Observe(string? reason, bool worked)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (reason is not null)
            {
                _cleanSince = null;
                if (_since is not null)
                    return null;

                _since = now;
                return new LlmOutageChange.Started(now, reason);
            }

            if (_since is not { } since || (!worked && _cleanSince is null))
                return null;

            _cleanSince ??= now;
            if (now - _cleanSince < RecoveryWindow)
                return null;

            var recovered = new LlmOutageChange.Recovered(since, _cleanSince.Value);
            (_since, _cleanSince) = (null, null);
            return recovered;
        }
    }
}

public abstract record LlmOutageChange
{
    public sealed record Started(DateTimeOffset Since, string Reason) : LlmOutageChange;

    // At — первый проход без сбоев
    public sealed record Recovered(DateTimeOffset Since, DateTimeOffset At) : LlmOutageChange;
}
