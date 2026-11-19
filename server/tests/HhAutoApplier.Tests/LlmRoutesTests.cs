using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Infrastructure;
using HhAutoApplier.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Tests;

// #104: модель и провайдер под задачу
public class LlmRoutesTests
{
    private static LlmOptions Main(params (string Name, LlmRouteOptions Route)[] routes)
    {
        var options = new LlmOptions { ApiKey = "main-key", Model = "openai/gpt-oss-120b", DailyRequestLimit = 300 };
        foreach (var (name, route) in routes)
            options.Routes[name] = route;
        return options;
    }

    [Fact]
    public void Route_InheritsUnsetValues_FromMainProvider()
    {
        var routes = LlmRoutes.Resolve(Main(("evaluation", new LlmRouteOptions { Model = "openai/gpt-oss-20b" })));

        var evaluation = Assert.Single(routes);
        Assert.Equal(LlmRoutes.Evaluation, evaluation.Key);
        Assert.Equal("openai/gpt-oss-20b", evaluation.Value.Model);
        Assert.Equal("main-key", evaluation.Value.ApiKey);
        Assert.Equal(LlmOptions.DefaultBaseUrl, evaluation.Value.BaseUrl);
        Assert.Equal(300, evaluation.Value.DailyRequestLimit);
    }

    [Fact]
    public void OtherProvider_GetsOnlyItsOwnKey_AndLimit()
    {
        var routes = LlmRoutes.Resolve(Main((LlmRoutes.Evaluation, new LlmRouteOptions
        {
            Provider = LlmOptions.GigaChatProvider, BaseUrl = "https://gigachat.devices.sberbank.ru/api/v1", ApiKey = "giga-key",
            Model = "GigaChat-2-Max", DailyRequestLimit = 500,
        })));

        var giga = routes[LlmRoutes.Evaluation];
        Assert.Equal("giga-key", giga.ApiKey);
        Assert.Equal(LlmOptions.GigaChatProvider, giga.GetProfile().Name);
        Assert.Equal(500, giga.DailyRequestLimit);
    }

    // Ключ основного провайдера не должен уйти чужому адресу
    [Fact]
    public void OtherProviderWithoutKey_IsConfigurationError()
    {
        var e = Assert.Throws<InvalidOperationException>(() => LlmRoutes.Resolve(Main((LlmRoutes.Letters,
            new LlmRouteOptions { BaseUrl = "https://openrouter.ai/api/v1", Model = "openai/gpt-oss-120b" }))));
        Assert.Contains("Letters", e.Message);
        Assert.Contains("ApiKey", e.Message);
    }

    [Fact]
    public void UnknownRoute_AndInvalidProvider_FailAtStartup()
    {
        Assert.Contains("Writing", Assert.Throws<InvalidOperationException>(() =>
            LlmRoutes.Resolve(Main(("Writing", new LlmRouteOptions { Model = "x" })))).Message);

        // ГигаЧат без своего адреса — та же ошибка, что и у основного провайдера, с именем маршрута
        Assert.Contains("Llm:Routes:Evaluation", Assert.Throws<InvalidOperationException>(() => LlmRoutes.Resolve(Main((LlmRoutes.Evaluation,
            new LlmRouteOptions { Provider = LlmOptions.GigaChatProvider, ApiKey = "k", Model = "GigaChat-2-Max" })))).Message);
    }

    [Fact]
    public void EmptyRoute_IsIgnored() => Assert.Empty(LlmRoutes.Resolve(Main((LlmRoutes.Dialogs, new LlmRouteOptions()))));

    [Fact]
    public void EveryPipelinePurpose_HasRoute()
    {
        Assert.Equal(LlmRoutes.Evaluation, LlmRoutes.Of(VacancyAssessment.Purpose));
        Assert.Equal(LlmRoutes.Evaluation, LlmRoutes.Of(LetterRequestExtraction.Purpose));
        Assert.Equal(LlmRoutes.Letters, LlmRoutes.Of(CoverLetterWriting.Purpose));
        Assert.Equal(LlmRoutes.Questionnaires, LlmRoutes.Of(QuestionnaireAnswering.Purpose));
        Assert.Equal(LlmRoutes.Dialogs, LlmRoutes.Of(DialogAnalysis.Purpose));
        Assert.Null(LlmRoutes.Of("check"));
    }

    private sealed class Fake(string name, bool configured = true) : ILlmClient
    {
        public List<string?> Purposes { get; } = [];

        public bool IsConfigured => configured;

        public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Purposes.Add(request.Purpose);
            return Task.FromResult(new LlmResult("ok", name, 1, 1));
        }
    }

    [Fact]
    public async Task Router_SendsPurposeToItsRoute_RestToMain()
    {
        var main = new Fake("main");
        var evaluation = new Fake("evaluation");
        var router = new RoutingLlmClient(main, new Dictionary<string, ILlmClient> { [LlmRoutes.Evaluation] = evaluation });

        Assert.Equal("evaluation", (await router.CompleteAsync(Ask(VacancyAssessment.Purpose), default)).Model);
        Assert.Equal("evaluation", (await router.CompleteAsync(Ask(LetterRequestExtraction.Purpose), default)).Model);
        Assert.Equal("main", (await router.CompleteAsync(Ask(CoverLetterWriting.Purpose), default)).Model);
        Assert.Equal("main", (await router.CompleteAsync(Ask(null), default)).Model);
    }

    [Fact]
    public void Router_RouteWithoutKey_FallsBackToMain_AndIsConfiguredByAny()
    {
        var main = new Fake("main", configured: false);
        var letters = new Fake("letters");
        var router = new RoutingLlmClient(main, new Dictionary<string, ILlmClient>
        {
            [LlmRoutes.Letters] = letters, [LlmRoutes.Dialogs] = new Fake("dialogs", configured: false),
        });

        Assert.True(router.IsConfigured);
        Assert.Same(letters, router.For(CoverLetterWriting.Purpose));
        Assert.Same(main, router.For(DialogAnalysis.Purpose));
    }

    private static LlmRequest Ask(string? purpose) => new([LlmMessage.User("?")], Purpose: purpose);

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

    // Маршрут из конфигурации доходит до своего адреса, ключа и модели; остальное — основной провайдер
    [Fact]
    public async Task Registration_RouteFromConfig_UsesOwnProviderKeyAndModel()
    {
        var main = new Recorder();
        var letters = new Recorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=unused",
            ["Hh:Enabled"] = "false",
            ["Llm:ApiKey"] = "groq-key",
            ["Llm:BaseUrl"] = "https://main.example.test/v1",
            ["Llm:Routes:Letters:BaseUrl"] = "https://letters.example.test/v1",
            ["Llm:Routes:Letters:ApiKey"] = "letters-key",
            ["Llm:Routes:Letters:Model"] = "deepseek-flash",
            ["Llm:Routes:Dialogs:Model"] = "",
        }).Build());
        services.AddSingleton(new LlmBudget(TimeProvider.System));
        services.AddKeyedSingleton("llm:Letters", new LlmBudget(TimeProvider.System));
        services.AddHttpClient(nameof(ILlmClient)).ConfigurePrimaryHttpMessageHandler(() => main);
        services.AddHttpClient("llm:Letters").ConfigurePrimaryHttpMessageHandler(() => letters);
        await using var sp = services.BuildServiceProvider();
        var llm = sp.GetRequiredService<ILlmClient>();

        await llm.CompleteAsync(Ask(CoverLetterWriting.Purpose), default);
        await llm.CompleteAsync(Ask(VacancyAssessment.Purpose), default);

        var (url, auth, model) = Assert.Single(letters.Requests);
        Assert.Equal(("https://letters.example.test/v1/chat/completions", "Bearer letters-key", "deepseek-flash"), (url, auth, model));
        var mainRequest = Assert.Single(main.Requests);
        Assert.Equal(("Bearer groq-key", "openai/gpt-oss-120b"), (mainRequest.Authorization, mainRequest.Model));
    }
}
