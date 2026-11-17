using HhAutoApplier.Application.Telegram;

namespace HhAutoApplier.Tests;

// #118: незнакомому чату бот отвечает не чаще раза в минуту
public class BotStrangerThrottleTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 17, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameChat_OncePerMinute()
    {
        var clock = new Clock(Start);
        var throttle = new BotStrangerThrottle(clock);

        Assert.True(throttle.TryAnswer(1));
        clock.Now = Start.AddSeconds(59);
        Assert.False(throttle.TryAnswer(1));
        // Пропущенное сообщение не сдвигает окно: отсчёт — от последнего ответа
        clock.Now = Start.AddMinutes(1);
        Assert.True(throttle.TryAnswer(1));
    }

    [Fact]
    public void Chats_Independent_ForgetResets()
    {
        var throttle = new BotStrangerThrottle(new Clock(Start));

        Assert.True(throttle.TryAnswer(1));
        Assert.True(throttle.TryAnswer(2));
        Assert.False(throttle.TryAnswer(1));

        // Чат привязали и отвязали — ответ сразу
        throttle.Forget(1);
        Assert.True(throttle.TryAnswer(1));
    }
}
