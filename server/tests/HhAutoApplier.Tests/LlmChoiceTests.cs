using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain;
using HhAutoApplier.Domain.Llm;
using HhAutoApplier.Infrastructure;
using HhAutoApplier.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Tests;

// #107: именованные подключения, выбор пользователя и маршрутизация по нему
public class LlmChoiceTests
{
    private sealed class Fake(string name, bool configured = true) : ILlmClient
    {
        public bool IsConfigured => configured;

        public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct) => Task.FromResult(new LlmResult("ok", name, 1, 1));
    }

    private static LlmRequest Ask(string purpose, string? provider) => new([LlmMessage.User("?")], Purpose: purpose, Provider: provider);

    [Fact]
    public async Task Router_ChosenConnection_WinsOverRoute_AndIsRecorded()
    {
        var router = new RoutingLlmClient(new Fake("main"), new Dictionary<string, ILlmClient> { [LlmRoutes.Evaluation] = new Fake("route") },
            new Dictionary<string, ILlmClient>(StringComparer.OrdinalIgnoreCase) { ["giga"] = new Fake("giga") });

        var chosen = await router.CompleteAsync(Ask(VacancyAssessment.Purpose, "GIGA"), default);
        var routed = await router.CompleteAsync(Ask(VacancyAssessment.Purpose, null), default);

        Assert.Equal(("giga", "giga"), (chosen.Model, chosen.Connection));
        Assert.Equal(("route", null), (routed.Model, routed.Connection));
    }

    // Подключение убрали из конфигурации или у него нет ключа: задача идёт маршрутом по умолчанию, а не встаёт
    [Fact]
    public async Task Router_UnknownOrUnconfiguredConnection_FallsBackToDefaultRoute()
    {
        var router = new RoutingLlmClient(new Fake("main"), new Dictionary<string, ILlmClient> { [LlmRoutes.Letters] = new Fake("letters") },
            new Dictionary<string, ILlmClient> { ["off"] = new Fake("off", configured: false) });

        Assert.Equal("letters", (await router.CompleteAsync(Ask(CoverLetterWriting.Purpose, "gone"), default)).Model);
        var off = await router.CompleteAsync(Ask(DialogAnalysis.Purpose, "off"), default);
        Assert.Equal(("main", null), (off.Model, off.Connection));
    }

    [Fact]
    public void Settings_KnownTasksOnly_EmptyMeansServerDefault()
    {
        var settings = UserLlmSettings.Create(Guid.NewGuid());

        settings.Choose(new Dictionary<string, string?> { ["evaluation"] = " giga ", ["Letters"] = "", ["Dialogs"] = null });

        var choice = Assert.Single(settings.Choices);
        Assert.Equal((LlmTasks.Evaluation, "giga"), (choice.Task, choice.Connection));
        Assert.Null(settings.ConnectionFor(LlmTasks.Letters));
        Assert.Throws<DomainException>(() => settings.Choose(new Dictionary<string, string?> { ["Translation"] = "giga" }));
    }

    [Fact]
    public void Connections_InheritLikeRoutes_AndValidateNames()
    {
        var main = new LlmOptions { ApiKey = "main-key", Model = "openai/gpt-oss-120b" };
        main.Providers["groq-small"] = new LlmRouteOptions { Model = "llama-3.1-8b" };
        main.Providers["gigachat"] = new LlmRouteOptions
        {
            Provider = LlmOptions.GigaChatProvider, BaseUrl = "https://gigachat.devices.sberbank.ru/api/v1", ApiKey = "giga-key", Model = "GigaChat-2-Max",
        };

        var connections = LlmConnections.Resolve(main);

        Assert.Equal(("main-key", "llama-3.1-8b"), (connections["groq-small"].ApiKey, connections["groq-small"].Model));
        Assert.Equal(("giga-key", LlmOptions.GigaChatProvider), (connections["GIGACHAT"].ApiKey, connections["gigachat"].GetProfile().Name));

        var bad = new LlmOptions { ApiKey = "k" };
        bad.Providers["мой ключ"] = new LlmRouteOptions { Model = "m" };
        Assert.Contains("Llm:Providers", Assert.Throws<InvalidOperationException>(() => LlmConnections.Resolve(bad)).Message);
        var foreignWithoutKey = new LlmOptions { ApiKey = "k" };
        foreignWithoutKey.Providers["router"] = new LlmRouteOptions { BaseUrl = "https://openrouter.ai/api/v1", Model = "m" };
        Assert.Contains("ApiKey", Assert.Throws<InvalidOperationException>(() => LlmConnections.Resolve(foreignWithoutKey)).Message);
    }

    // Подключение из конфигурации доходит до своего адреса, ключа и модели; список подключений — без ключей
    [Fact]
    public async Task Registration_ConnectionFromConfig_UsesOwnKey_AndIsListed()
    {
        var main = new Recorder();
        var fast = new Recorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=unused",
            ["Hh:Enabled"] = "false",
            ["Llm:ApiKey"] = "groq-key",
            ["Llm:BaseUrl"] = "https://main.example.test/v1",
            ["Llm:Providers:fast:BaseUrl"] = "https://fast.example.test/v1",
            ["Llm:Providers:fast:ApiKey"] = "fast-key",
            ["Llm:Providers:fast:Model"] = "fast-model",
            ["Llm:Providers:fast:DailyRequestLimit"] = "50",
        }).Build());
        // Счётчики без журнала: базы в тесте нет
        services.AddSingleton(new LlmBudget(TimeProvider.System));
        services.AddKeyedSingleton("llm-provider:fast", new LlmBudget(TimeProvider.System));
        services.AddHttpClient(nameof(ILlmClient)).ConfigurePrimaryHttpMessageHandler(() => main);
        services.AddHttpClient("llm-provider:fast").ConfigurePrimaryHttpMessageHandler(() => fast);
        await using var sp = services.BuildServiceProvider();

        var result = await sp.GetRequiredService<ILlmClient>().CompleteAsync(Ask(CoverLetterWriting.Purpose, "fast"), default);

        Assert.Equal("fast", result.Connection);
        Assert.Equal(("https://fast.example.test/v1/chat/completions", "Bearer fast-key", "fast-model"), Assert.Single(fast.Requests));
        Assert.Empty(main.Requests);
        var listed = Assert.Single(sp.GetRequiredService<ILlmConnections>().Named());
        Assert.Equal(("fast", "openai", "fast-model", true, 1, 50), (listed.Name, listed.Provider, listed.Model, listed.Available, listed.UsedToday, listed.DailyLimit));
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, string Model)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body.RootElement.GetProperty("model").GetString()!));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"model":"m","choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}"""),
            };
        }
    }
}
