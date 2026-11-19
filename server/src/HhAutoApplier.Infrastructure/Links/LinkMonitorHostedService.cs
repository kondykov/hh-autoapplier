using HhAutoApplier.Application.Commands.Links;
using HhAutoApplier.Application.Commands.Notifications;
using HhAutoApplier.Application.Links;
using HhAutoApplier.Domain.Links;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Infrastructure.Links;

// Проверка связи по направлениям (#132): раз в 2 минуты, при сбое — раз в 30 с. Итог — в LinkStatus и журнал сбоев;
// когда Telegram отвечает — досылка уведомлений, ждавших связи. Узлы Happ не переключает: это приложение владельца
public class LinkMonitorHostedService(
    LinkTargets targets,
    LinkStatus status,
    IServiceScopeFactory scopes,
    IOptions<LinkOptions> options,
    TimeProvider clock,
    ILogger<LinkMonitorHostedService> logger
) : BackgroundService
{
    // Первая проверка — когда приложение поднялось, а не в момент старта
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        var enabled = o.Enabled && targets.Any;
        status.Configure(enabled, targets.ProxyDisplay);
        if (!enabled)
        {
            logger.LogInformation("Связь: проверка выключена ({Reason})", o.Enabled ? "нет направлений" : "Links:Enabled=false");
            return;
        }

        logger.LogInformation("Связь: проверка {Links}, прокси {Proxy}", string.Join(", ", targets.Directions.Select(t => t.Proxy is null ? $"{t.Link} (напрямую)" : t.Link)),
            targets.ProxyDisplay ?? "нет");
        var prober = new LinkProber(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 60)));
        var interval = TimeSpan.FromSeconds(Math.Clamp(o.IntervalSeconds, 10, 3600));
        var failureInterval = TimeSpan.FromSeconds(Math.Clamp(o.FailureIntervalSeconds, 5, 3600));

        try
        {
            await Task.Delay(StartDelay, clock, stop);
            while (!stop.IsCancellationRequested)
            {
                var failing = false;
                try
                {
                    failing = await CheckAsync(prober, stop);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    logger.LogError(e, "Связь: проверка не удалась");
                }

                await Task.Delay(failing ? failureInterval : interval, clock, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    // true — что-то отказало: следующая проверка раньше
    private async Task<bool> CheckAsync(LinkProber prober, CancellationToken ct)
    {
        var startedAt = clock.GetUtcNow();
        var checks = targets.Directions.Select(t => prober.ProbeAsync(t, ct)).ToList();
        if (targets.Proxy is { } proxy)
            checks.Add(prober.ProbeProxyAsync(proxy, ct));
        LinkProbe[] probes = await Task.WhenAll(checks);

        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<RecordLinkCheckCommandHandler>().Handle(new RecordLinkCheckCommand(probes), ct);
        foreach (var failure in result.Started)
            logger.LogWarning("Связь: сбой {Subject} ({Scope}) — {Reason}: {Detail}", failure.Subject, failure.Scope, failure.Reason, failure.Detail);
        foreach (var subject in result.Ended)
            logger.LogInformation("Связь: {Subject} — восстановлена", subject);

        if (result.TelegramOk)
        {
            var resumed = await scope.ServiceProvider.GetRequiredService<ResumeWaitingNotificationsCommandHandler>()
                .Handle(new ResumeWaitingNotificationsCommand(startedAt), ct);
            if (resumed.Resumed + resumed.Digested + resumed.Expired > 0)
                logger.LogInformation("Связь: досылка уведомлений — важных {Resumed}, в сводку {Digested}, устарело {Expired}",
                    resumed.Resumed, resumed.Digested, resumed.Expired);
        }

        return result.Verdict.Failing;
    }
}
