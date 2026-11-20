using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Commands.Dialogs;
using HhAutoApplier.Application.Commands.Llm;
using HhAutoApplier.Application.Commands.Notifications;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Domain.Notifications;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HhAutoApplier.Infrastructure.Llm;

// LLM-конвейер раз в минуту: оценка новых, ответы на анкеты, письма для очереди, уведомления о проверке, разбор ответов
// работодателей. Очередь — строки в БД, ждущие LLM. Отдельно от часового сбора: выдача из расширения (POST /api/import)
// и чаты оцениваются через минуту, а не со следующим сбором (2026-10-27 импорт ждал оценки больше двух часов).
// Один проход за раз — вместе с ручными /api/llm/* (LlmPipelineGate, #54); решение пользователя, принятое, пока LLM
// отвечала, не затирается (xmin, SaveOrDiscardAsync)
public class LlmPipelineHostedService(
    IServiceScopeFactory scopes,
    ILogger<LlmPipelineHostedService> logger
) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // За проход: больше — до следующей минуты (провайдер ограничивает частоту запросов)
    private const int Batch = 20;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "LLM-конвейер завершился ошибкой");
            }

            await Task.Delay(Interval, stop);
        }
    }

    // День последней очистки журнала LlmCalls (#113): раз в сутки, первым проходом дня
    private DateOnly _purgedOn;

    private async Task RunOnceAsync(IServiceProvider services, CancellationToken stop)
    {
        var today = DateOnly.FromDateTime(services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime);
        if (today != _purgedOn)
        {
            var purged = await services.GetRequiredService<PurgeLlmCallsCommandHandler>().Handle(new PurgeLlmCallsCommand(), stop);
            _purgedOn = today;
            if (purged > 0)
                logger.LogInformation("Журнал LlmCalls: удалено {Count} записей старше {Days} дней", purged, PurgeLlmCallsCommand.Retention.TotalDays);
        }

        if (services.GetRequiredService<ILlmClient>().IsConfigured)
        {
            // Без работы — без вызовов: обработчики пишут сводку в лог, раз в минуту это был бы шум
            var db = services.GetRequiredService<IAppDbContext>();
            var results = new List<object?>();
            if (await db.JobApplications.AnyAsync(a => a.Status == VacancyStatus.Pending, stop))
                results.Add((await services.GetRequiredService<EvaluatePendingVacanciesCommandHandler>().Handle(new EvaluatePendingVacanciesCommand { Limit = Batch }, stop)).Value);
            if (await db.JobApplications.AnyAsync(a => a.Status == VacancyStatus.Questionnaire, stop))
                results.Add((await services.GetRequiredService<AnswerQuestionnairesCommandHandler>().Handle(new AnswerQuestionnairesCommand { Limit = Batch }, stop)).Value);
            if (await db.JobApplications.AnyAsync(a => (a.Status == VacancyStatus.Queued || a.Status == VacancyStatus.Review) && a.CoverLetter == null, stop))
                results.Add((await services.GetRequiredService<WriteCoverLettersCommandHandler>().Handle(new WriteCoverLettersCommand { Limit = Batch }, stop)).Value);
            if (await db.EmployerMessages.AnyAsync(m => m.Kind == null, stop))
                results.Add((await services.GetRequiredService<AnalyzeDialogsCommandHandler>().Handle(new AnalyzeDialogsCommand { Limit = Batch }, stop)).Value);

            var reason = results.Select(StoppedBy).FirstOrDefault(r => r is not null);
            if (services.GetRequiredService<LlmOutage>().Observe(reason, results.Count > 0) is { } change)
                await NotifyAsync(services.GetRequiredService<Notifier>(), change, stop);

            await services.GetRequiredService<LlmUserQuota>().NotifyExhaustedAsync(stop);
            await services.GetRequiredService<LlmQualityMonitor>().NotifyIfFrequentAsync(stop);
        }

        // После писем: в сообщении о проверке уже есть письмо
        await services.GetRequiredService<NotifyReviewsCommandHandler>().Handle(new NotifyReviewsCommand(), stop);
    }

    // Сообщение на перерыв и на восстановление (#108); ключ — начало перерыва, повтор того же перерыва не пишется
    private static Task NotifyAsync(Notifier notifier, LlmOutageChange change, CancellationToken stop) => change switch
    {
        LlmOutageChange.Started s => notifier.NotifyAdminsAsync(NotificationKind.LlmUnavailable, $"llm:{s.Since:yyyy-MM-ddTHH:mm}",
            $"{s.Reason}. Оценка, анкеты, письма и разбор ответов продолжатся позже — сообщу, когда LLM снова ответит.", stop),
        LlmOutageChange.Recovered r => notifier.NotifyAdminsAsync(NotificationKind.LlmUnavailable, $"llm-ok:{r.Since:yyyy-MM-ddTHH:mm}",
            $"LLM снова отвечает: сбой с {MoscowTime.Format(r.Since)} до {MoscowTime.Format(r.At)}. Оценка, анкеты, письма и разбор ответов продолжаются.", stop),
        _ => Task.CompletedTask,
    };

    // Почему проход LLM остановился раньше (лимит, провайдер); null — прошёл всё
    private static string? StoppedBy(object? result) => result switch
    {
        LlmEvaluationResultDto r => r.StoppedBy,
        QuestionnaireAnswersResultDto r => r.StoppedBy,
        CoverLettersResultDto r => r.StoppedBy,
        DialogsAnalysisResultDto r => r.StoppedBy,
        LlmUnavailableError e => e.Message,
        _ => null,
    };
}
