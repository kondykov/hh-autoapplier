using System.Net;
using System.Net.Security;
using HhAutoApplier.Application;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Commands.Collecting;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain.Llm;
using HhAutoApplier.Infrastructure.Collecting;
using HhAutoApplier.Infrastructure.Identity;
using HhAutoApplier.Infrastructure.Links;
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
        services.Configure<StageOptions>(config.GetSection(StageOptions.Section));
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
        // Именованные подключения (#107): выбор пользователя по задачам, у каждого тоже свой клиент, счётчик и пауза
        var llmConnections = LlmConnections.Resolve(llmOptions);
        // Счётчик после перезапуска продолжается по журналу: основной — без назначений маршрутов, маршрут — только свои;
        // запросы через подключение пользователя — только в счёт подключения
        static Func<DateOnly, int> UsedToday(IServiceProvider sp, Func<IQueryable<LlmCall>, IQueryable<LlmCall>> filter) => day =>
        {
            using var scope = sp.CreateScope();
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var calls = scope.ServiceProvider.GetRequiredService<AppDbContext>().LlmCalls.Where(c => c.CreatedAt >= from && c.CreatedAt < from.AddDays(1));
            // Только запросы, дошедшие до модели: отказы до запроса («исчерпан лимит», сеть, пауза) лимит не тратят — иначе после
            // перезапуска сотни таких записей за сбойный день считались бы расходом и сразу закрывали лимит
            return filter(calls).Count(c => c.Model != null && c.Model != "");
        };
        services.AddSingleton(sp => new LlmBudget(sp.GetRequiredService<TimeProvider>(),
            UsedToday(sp, q => q.Where(c => c.Connection == null && !routedPurposes.Contains(c.Purpose)))));

        var gigaChat = new[] { llmOptions }.Concat(llmRoutes.Values).Concat(llmConnections.Values)
            .Any(o => o.GetProfile().Name == LlmOptions.GigaChatProvider);
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

        // Состояние маршрута и подключения живёт всё время работы приложения: счётчик и пауза, токен ГигаЧата — keyed-синглтоны
        // по ключу llm:<маршрут> или llm-provider:<имя>
        void AddEndpoint(string key, LlmOptions options, Func<IQueryable<LlmCall>, IQueryable<LlmCall>> used)
        {
            var http = services.AddHttpClient(key);
            services.AddKeyedSingleton(key, (sp, _) => new LlmBudget(sp.GetRequiredService<TimeProvider>(), UsedToday(sp, used)));
            if (options.GetProfile().Name != LlmOptions.GigaChatProvider)
                return;
            http.ConfigurePrimaryHttpMessageHandler(() => GigaChatTls.CreateHandler(root!));
            services.AddKeyedSingleton<ILlmTokenSource>(key, (sp, _) =>
                new GigaChatTokenSource(sp.GetRequiredService<IHttpClientFactory>(), Options.Create(options), sp.GetRequiredService<TimeProvider>()));
        }

        foreach (var (name, options) in llmRoutes)
        {
            var purposes = LlmRoutes.PurposesOf(name).ToList();
            AddEndpoint(RouteClient(name), options, q => q.Where(c => c.Connection == null && purposes.Contains(c.Purpose)));
        }

        foreach (var (name, options) in llmConnections)
            AddEndpoint(ConnectionClient(name), options, q => q.Where(c => c.Connection == name));

        static ILlmClient EndpointClient(IServiceProvider sp, string key, LlmOptions options) => new OpenAiCompatibleClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(key), Options.Create(options),
            sp.GetRequiredKeyedService<LlmBudget>(key), sp.GetRequiredService<ILogger<OpenAiCompatibleClient>>(),
            tokens: sp.GetKeyedService<ILlmTokenSource>(key));

        services.AddTransient<ILlmClient>(sp => new RoutingLlmClient(
            sp.GetRequiredService<OpenAiCompatibleClient>(),
            llmRoutes.ToDictionary(r => r.Key, r => EndpointClient(sp, RouteClient(r.Key), r.Value)),
            llmConnections.ToDictionary(c => c.Key, c => EndpointClient(sp, ConnectionClient(c.Key), c.Value), StringComparer.OrdinalIgnoreCase),
            sp.GetRequiredService<ILogger<RoutingLlmClient>>()));

        services.AddSingleton<ILlmConnections>(sp => new LlmConnectionRegistry(
            new LlmEndpoint(LlmConnectionRegistry.MainName, llmOptions, sp.GetRequiredService<LlmBudget>()),
            llmRoutes.ToDictionary(r => r.Key, r => new LlmEndpoint(r.Key, r.Value, sp.GetRequiredKeyedService<LlmBudget>(RouteClient(r.Key)))),
            llmConnections.Select(c => new LlmEndpoint(c.Key, c.Value, sp.GetRequiredKeyedService<LlmBudget>(ConnectionClient(c.Key)))).ToList()));

        // Telegram: без токена бот выключен, уведомления не пишутся
        services.Configure<TelegramOptions>(config.GetSection(TelegramOptions.Section));
        // Логи этого клиента — только от Warning (appsettings): в адресе запроса Bot API — токен бота
        services.AddHttpClient(nameof(TelegramBot));
        services.AddSingleton<TelegramBot>();
        services.AddSingleton<ITelegramBot>(sp => sp.GetRequiredService<TelegramBot>());
        services.AddHostedService<TelegramHostedService>();

        // Проверка связи по направлениям (#132): Bot API и провайдеры LLM с ключом — тем же прокси, что у HTTP-клиентов
        services.Configure<LinkOptions>(config.GetSection(LinkOptions.Section));
        var telegramOn = !string.IsNullOrWhiteSpace(config.GetSection(TelegramOptions.Section).Get<TelegramOptions>()?.BotToken);
        var llmWithKey = new[] { llmOptions }.Concat(llmRoutes.Values).Concat(llmConnections.Values).Where(o => !string.IsNullOrWhiteSpace(o.ApiKey)).ToList();
        // ГигаЧат — с тем же доверием к корню Минцифры, что у его HTTP-клиентов: иначе проверка видит «недоверенный сертификат» (#138)
        Dictionary<string, RemoteCertificateValidationCallback>? gigaChatCertificates = null;
        if (root is not null)
        {
            RemoteCertificateValidationCallback minTsifry = (_, certificate, chain, errors) => GigaChatTls.Validate(root, certificate, chain, errors);
            gigaChatCertificates = llmWithKey.Where(o => o.GetProfile().Name == LlmOptions.GigaChatProvider)
                .Select(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var u) ? u.Host : null)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(h => h, _ => minTsifry, StringComparer.OrdinalIgnoreCase);
        }
        services.AddSingleton(new LinkTargets(LinkTargets.Endpoints(telegramOn, llmWithKey.Select(o => o.BaseUrl)), HttpClient.DefaultProxy, gigaChatCertificates));
        services.AddHostedService<LinkMonitorHostedService>();

        services.AddSingleton<CollectionProgress>();
        services.AddSingleton<ICollectionBackground, CollectionBackground>();
        services.AddHostedService<CollectorHostedService>();
        services.AddHostedService<LlmPipelineHostedService>();
        return services;
    }

    private static string RouteClient(string route) => $"llm:{route}";

    private static string ConnectionClient(string name) => $"llm-provider:{name}";
}
