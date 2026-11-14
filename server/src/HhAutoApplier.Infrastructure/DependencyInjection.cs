using System.Net;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Commands.Collecting;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain.Llm;
using HhAutoApplier.Infrastructure.Collecting;
using HhAutoApplier.Infrastructure.Identity;
using HhAutoApplier.Infrastructure.Llm;
using HhAutoApplier.Infrastructure.Persistence;
using HhAutoApplier.Infrastructure.Sources.Hh;
using HhAutoApplier.Infrastructure.Telegram;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HhAutoApplier.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CollectorOptions>(config.GetSection(CollectorOptions.Section));
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(config.GetConnectionString("Default")));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        // Пользователи и роли: ASP.NET Core Identity поверх той же базы. Схемы входа (cookie, ключ API) — в Api
        services.Configure<AdminOptions>(config.GetSection(AdminOptions.Section));
        services.AddIdentityCore<AppUser>(o =>
            {
                // Длина важнее набора классов символов; пароль задаёт человек, не генератор
                o.Password.RequiredLength = 10;
                o.Password.RequiredUniqueChars = 4;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.User.RequireUniqueEmail = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<AppSignInManager>()
            .AddErrorDescriber<RussianIdentityErrorDescriber>();
        services.AddScoped<IUserService, UserService>();

        // Источники вакансий: новый агрегатор = своя реализация IVacancySource и регистрация здесь
        var hh = config.GetSection(HhOptions.Section);
        services.Configure<HhOptions>(hh);
        if (hh.GetValue("Enabled", true))
        {
            services.AddHttpClient<HhWebSource>()
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
            services.AddTransient<IVacancySource>(sp => sp.GetRequiredService<HhWebSource>());
        }

        // LLM: OpenAI-совместимый провайдер (по умолчанию Groq) или ГигаЧат; без ключа IsConfigured = false
        var llm = config.GetSection(LlmOptions.Section);
        services.Configure<LlmOptions>(llm);
        var llmOptions = llm.Get<LlmOptions>() ?? new LlmOptions();
        llmOptions.EnsureValid();
        // Маршруты под задачи (#104): у каждого свой клиент, счётчик дневного лимита и пауза
        var llmRoutes = LlmRoutes.Resolve(llmOptions);
        var routedPurposes = llmRoutes.Keys.SelectMany(LlmRoutes.PurposesOf).ToList();
        // Счётчик после перезапуска продолжается по журналу: основной — без назначений маршрутов, маршрут — только свои
        static Func<DateOnly, int> UsedToday(IServiceProvider sp, Func<IQueryable<LlmCall>, IQueryable<LlmCall>> filter) => day =>
        {
            using var scope = sp.CreateScope();
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var calls = scope.ServiceProvider.GetRequiredService<AppDbContext>().LlmCalls.Where(c => c.CreatedAt >= from && c.CreatedAt < from.AddDays(1));
            return filter(calls).Count();
        };
        services.AddSingleton(sp => new LlmBudget(sp.GetRequiredService<TimeProvider>(), UsedToday(sp, q => q.Where(c => !routedPurposes.Contains(c.Purpose)))));

        var gigaChat = llmOptions.GetProfile().Name == LlmOptions.GigaChatProvider
                       || llmRoutes.Values.Any(o => o.GetProfile().Name == LlmOptions.GigaChatProvider);
        var root = gigaChat ? GigaChatTls.LoadRoot() : null;
        if (root is not null)
            services.AddHttpClient(GigaChatTokenSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => GigaChatTls.CreateHandler(root));

        // Имя клиента — прежнее (ILlmClient): по нему настраивается обработчик основного провайдера
        var mainHttp = services.AddHttpClient<OpenAiCompatibleClient>(nameof(ILlmClient));
        if (llmOptions.GetProfile().Name == LlmOptions.GigaChatProvider)
        {
            mainHttp.ConfigurePrimaryHttpMessageHandler(() => GigaChatTls.CreateHandler(root!));
            services.AddSingleton<ILlmTokenSource, GigaChatTokenSource>();
        }

        // Состояние маршрута живёт всё время работы приложения: счётчик и пауза, токен ГигаЧата — keyed-синглтоны по имени маршрута
        foreach (var (name, options) in llmRoutes)
        {
            var http = services.AddHttpClient(RouteClient(name));
            var purposes = LlmRoutes.PurposesOf(name).ToList();
            services.AddKeyedSingleton(name, (sp, _) => new LlmBudget(sp.GetRequiredService<TimeProvider>(), UsedToday(sp, q => q.Where(c => purposes.Contains(c.Purpose)))));
            if (options.GetProfile().Name != LlmOptions.GigaChatProvider)
                continue;
            http.ConfigurePrimaryHttpMessageHandler(() => GigaChatTls.CreateHandler(root!));
            services.AddKeyedSingleton<ILlmTokenSource>(name, (sp, _) =>
                new GigaChatTokenSource(sp.GetRequiredService<IHttpClientFactory>(), Options.Create(options), sp.GetRequiredService<TimeProvider>()));
        }

        services.AddTransient<ILlmClient>(sp => new RoutingLlmClient(
            sp.GetRequiredService<OpenAiCompatibleClient>(),
            llmRoutes.ToDictionary(r => r.Key, r => (ILlmClient)new OpenAiCompatibleClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(RouteClient(r.Key)), Options.Create(r.Value),
                sp.GetRequiredKeyedService<LlmBudget>(r.Key), sp.GetRequiredService<ILogger<OpenAiCompatibleClient>>(),
                tokens: sp.GetKeyedService<ILlmTokenSource>(r.Key)))));

        // Telegram: без токена бот выключен, уведомления не пишутся
        services.Configure<TelegramOptions>(config.GetSection(TelegramOptions.Section));
        // Логи этого клиента — только от Warning (appsettings): в адресе запроса Bot API — токен бота
        services.AddHttpClient(nameof(TelegramBot));
        services.AddSingleton<TelegramBot>();
        services.AddSingleton<ITelegramBot>(sp => sp.GetRequiredService<TelegramBot>());
        services.AddHostedService<TelegramHostedService>();

        services.AddSingleton<CollectionProgress>();
        services.AddSingleton<ICollectionBackground, CollectionBackground>();
        services.AddHostedService<CollectorHostedService>();
        services.AddHostedService<LlmPipelineHostedService>();
        return services;
    }

    private static string RouteClient(string route) => $"llm:{route}";
}
