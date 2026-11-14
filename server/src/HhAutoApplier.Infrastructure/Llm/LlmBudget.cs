namespace HhAutoApplier.Infrastructure.Llm;

// Счётчик запросов к LLM за сутки (UTC). usedToday — сколько уже было в журнале LlmCalls: после перезапуска счёт продолжается
public class LlmBudget(TimeProvider clock, Func<DateOnly, int>? usedToday = null)
{
    private readonly object _lock = new();

    private DateOnly _day;

    private int _used;

    // До этого момента провайдер ограничил частоту (429 с долгим Retry-After, #85): общий для всех конвейеров и ручных /api/llm/*
    private DateTimeOffset _throttledUntil;

    public DateTimeOffset? ThrottledUntil
    {
        get
        {
            lock (_lock)
                return _throttledUntil > clock.GetUtcNow() ? _throttledUntil : null;
        }
    }

    public void ThrottleFor(TimeSpan pause)
    {
        lock (_lock)
        {
            var until = clock.GetUtcNow() + pause;
            if (until > _throttledUntil)
                _throttledUntil = until;
        }
    }

    // Запрос не дошёл до модели (провайдер ограничил частоту): единица лимита возвращается
    public void Return()
    {
        lock (_lock)
        {
            Roll();
            if (_used > 0)
                _used--;
        }
    }

    public int Used
    {
        get
        {
            lock (_lock)
            {
                Roll();
                return _used;
            }
        }
    }

    // limit 0 — без ограничения
    public bool TryTake(int limit)
    {
        lock (_lock)
        {
            Roll();
            if (limit > 0 && _used >= limit)
                return false;

            _used++;
            return true;
        }
    }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (today == _day)
            return;

        _day = today;
        _used = usedToday?.Invoke(today) ?? 0;
    }
}
