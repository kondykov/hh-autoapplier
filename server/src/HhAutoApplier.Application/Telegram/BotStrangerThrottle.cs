using System.Collections.Concurrent;

namespace HhAutoApplier.Application.Telegram;

// Незнакомому (непривязанному) чату бот отвечает «не привязан» не чаще раза в минуту (#118): остальное молча пропускается,
// чтобы спам не превращался в поток ответов и запросов. Состояние — в памяти: после перезапуска ответ придёт снова, это не страшно.
// Привязка чата сбрасывает его запись: отвязанный потом чат сразу получит ответ
public class BotStrangerThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // Сколько чатов помнить: дальше старые записи вычищаются
    private const int MaxChats = 10_000;

    private readonly ConcurrentDictionary<long, DateTimeOffset> _answered = new();

    // Можно ли ответить этому чату сейчас; если да — запоминает время ответа
    public bool TryAnswer(long chatId)
    {
        var now = clock.GetUtcNow();
        if (_answered.TryGetValue(chatId, out var last) && now - last < Interval)
            return false;

        _answered[chatId] = now;
        if (_answered.Count > MaxChats)
            foreach (var (id, at) in _answered)
                if (now - at >= Interval)
                    _answered.TryRemove(id, out _);
        return true;
    }

    public void Forget(long chatId) => _answered.TryRemove(chatId, out _);

    public void Clear() => _answered.Clear();
}
