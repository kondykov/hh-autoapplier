using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Telegram;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Application;

public static class DependencyInjection
{
    // Обработчики команд и запросов и экстракторы регистрируются автоматически: новый класс в папке — без правок здесь
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssemblyOf<ApplicationAssembly>()
            .AddClasses(classes => classes.AssignableToAny(typeof(ICommandHandler<,>), typeof(IQueryHandler<,>), typeof(IExtractor<,>)))
            .AsImplementedInterfaces()
            .AsSelf()
            .WithScopedLifetime());

        // Бот Telegram (#115): пользователь чата для сценариев и разделы бота — новый раздел регистрируется сам
        services.AddScoped<ActingUser>();
        services.AddScoped<BotChat>();
        services.AddScoped<BotDigest>();
        services.Scan(scan => scan
            .FromAssemblyOf<ApplicationAssembly>()
            .AddClasses(classes => classes.AssignableTo<IBotSection>())
            .AsSelfWithInterfaces()
            .WithScopedLifetime());

        services.AddScoped<Notifier>();
        services.AddScoped<LlmUserQuota>();
        services.AddScoped<LlmQualityMonitor>();
        services.AddSingleton<LlmPipelineGate>();
        services.AddSingleton<LlmOutage>();
        services.AddSingleton<BotStrangerThrottle>();
        services.AddSingleton(VacancyEvaluator.Default);
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}

// Метка сборки для сканирования
public sealed class ApplicationAssembly;
