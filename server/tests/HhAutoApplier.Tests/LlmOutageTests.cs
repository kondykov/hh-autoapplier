using HhAutoApplier.Application.Llm;
using HhAutoApplier.Application.Notifications;

namespace HhAutoApplier.Tests;

// #108: сообщение о сбое LLM — при смене состояния, о восстановлении — после 10 минут без сбоев; время — московское
public class LlmOutageTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Утро 2026-11-15: сервер включился в 07:58 UTC, VPN подключился к 08:15 UTC
    private static readonly DateTimeOffset Boot = new(2026, 11, 15, 7, 58, 0, TimeSpan.Zero);

    [Fact]
    public void MorningOutage_OneStart_ThenRecoveryAfterCleanWindow()
    {
        var clock = new Clock(Boot);
        var outage = new LlmOutage(clock);

        var started = Assert.IsType<LlmOutageChange.Started>(outage.Observe("LLM недоступна: сеть", worked: true));
        Assert.Equal(Boot, started.Since);

        // Сбои каждую минуту до 08:15 — без новых сообщений
        for (var i = 1; i <= 16; i++)
        {
            clock.Now = Boot.AddMinutes(i);
            Assert.Null(outage.Observe("LLM недоступна: сеть", worked: true));
        }

        clock.Now = Boot.AddMinutes(17);
        Assert.Null(outage.Observe(null, worked: true));
        clock.Now = Boot.AddMinutes(26);
        Assert.Null(outage.Observe(null, worked: true));

        // Работа кончилась, проходы пустые — окно всё равно досчитывается
        clock.Now = Boot.AddMinutes(27);
        var recovered = Assert.IsType<LlmOutageChange.Recovered>(outage.Observe(null, worked: false));
        Assert.Equal(Boot, recovered.Since);
        Assert.Equal(Boot.AddMinutes(17), recovered.At);

        // Дальше — тишина
        clock.Now = Boot.AddMinutes(28);
        Assert.Null(outage.Observe(null, worked: true));
    }

    [Fact]
    public void FailureInsideRecoveryWindow_ResetsIt_WithoutNewMessage()
    {
        var clock = new Clock(Boot);
        var outage = new LlmOutage(clock);
        Assert.NotNull(outage.Observe("сбой", worked: true));

        clock.Now = Boot.AddMinutes(5);
        Assert.Null(outage.Observe(null, worked: true));
        clock.Now = Boot.AddMinutes(10);
        Assert.Null(outage.Observe("сбой", worked: true));
        clock.Now = Boot.AddMinutes(16);
        Assert.Null(outage.Observe(null, worked: true));
        clock.Now = Boot.AddMinutes(25);
        Assert.Null(outage.Observe(null, worked: true));

        clock.Now = Boot.AddMinutes(26);
        Assert.Equal(Boot.AddMinutes(16), Assert.IsType<LlmOutageChange.Recovered>(outage.Observe(null, worked: true)).At);
    }

    [Fact]
    public void NoOutage_IdleOrClean_NoMessages()
    {
        var clock = new Clock(Boot);
        var outage = new LlmOutage(clock);
        Assert.Null(outage.Observe(null, worked: true));
        Assert.Null(outage.Observe(null, worked: false));

        // Пустые проходы без чистого прохода не означают восстановления
        Assert.NotNull(outage.Observe("сбой", worked: true));
        clock.Now = Boot.AddHours(1);
        Assert.Null(outage.Observe(null, worked: false));
    }

    [Fact]
    public void NextOutage_AfterRecovery_IsReportedAgain()
    {
        var clock = new Clock(Boot);
        var outage = new LlmOutage(clock);
        outage.Observe("сбой", worked: true);
        clock.Now = Boot.AddMinutes(1);
        outage.Observe(null, worked: true);
        clock.Now = Boot.AddMinutes(11);
        Assert.IsType<LlmOutageChange.Recovered>(outage.Observe(null, worked: true));

        clock.Now = Boot.AddHours(3);
        Assert.Equal(Boot.AddHours(3), Assert.IsType<LlmOutageChange.Started>(outage.Observe("сбой", worked: true)).Since);
    }

    [Fact]
    public void MoscowTime_IsUtcPlusThree_WithMark()
    {
        Assert.Equal("11:15 МСК", MoscowTime.Format(new DateTimeOffset(2026, 11, 15, 8, 15, 0, TimeSpan.Zero)));
        Assert.Equal("02:30 МСК", MoscowTime.Format(new DateTimeOffset(2026, 11, 14, 23, 30, 0, TimeSpan.Zero)));
    }
}
