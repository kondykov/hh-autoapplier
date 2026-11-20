using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HhAutoApplier.Application.Llm;
using HhAutoApplier.Domain.Llm;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Tests.Api;

// #107: пользователь выбирает подключение LLM по задаче, разовый прогон «Оценить заново через …»
[Collection(ApiCollection.Name)]
public class LlmChoiceApiTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Password = "user-password-1";

    private readonly HttpClient _admin = factory.CreateApiClient();

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(Guid Id, HttpClient Client)> User(string email = "b@test.local")
    {
        var user = await Json(await _admin.PostAsJsonAsync("/api/users", new { email, password = Password, role = "User" }));
        return (Guid.Parse(user.GetProperty("id").GetString()!), factory.CreateLoggedInClient(email, Password));
    }

    private static Task<HttpResponseMessage> Choose(HttpClient client, object choices) =>
        client.PutAsJsonAsync("/api/profile/llm", new { choices });

    // Список подключений — любому вошедшему, без ключей и адресов; задачи без выбора — чем их выполняет сервер
    [Fact]
    public async Task Providers_ForAnyUser_WithoutSecrets()
    {
        var (_, b) = await User();

        var response = await b.GetAsync("/api/llm/providers");
        var body = await response.Content.ReadAsStringAsync();
        var json = await Json(response);

        var providers = json.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal([FakeLlmConnections.Fast, FakeLlmConnections.Giga], providers.Select(p => p.GetProperty("name").GetString()));
        var giga = providers[1];
        Assert.False(giga.GetProperty("available").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, giga.GetProperty("pausedUntil").ValueKind);
        Assert.Equal((3, 300), (providers[0].GetProperty("usedToday").GetInt32(), providers[0].GetProperty("dailyLimit").GetInt32()));
        Assert.Equal(LlmTasks.All, json.GetProperty("defaults").EnumerateArray().Select(d => d.GetProperty("task").GetString()));
        Assert.DoesNotContain("apiKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baseUrl", body, StringComparison.OrdinalIgnoreCase);
    }

    // Свои настройки — только свои: сохранить можно только себе, чужие читает только администратор
    [Fact]
    public async Task Settings_AreOwn_AdminReadsOthers_UserCannot()
    {
        var (bId, b) = await User();

        var saved = await Json(await Choose(b, new { Evaluation = FakeLlmConnections.Giga, Letters = "", Dialogs = (string?)null }));
        Assert.Equal(FakeLlmConnections.Giga, saved.GetProperty("choices").GetProperty("Evaluation").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("choices").GetProperty("Letters").ValueKind);

        // Администратор у себя видит пустой выбор, у B — выбор B
        var own = await Json(await _admin.GetAsync("/api/profile/llm"));
        Assert.All(LlmTasks.All, t => Assert.Equal(JsonValueKind.Null, own.GetProperty("choices").GetProperty(t).ValueKind));
        var ofB = await Json(await _admin.GetAsync($"/api/profile/llm?userId={bId}"));
        Assert.Equal(FakeLlmConnections.Giga, ofB.GetProperty("choices").GetProperty("Evaluation").GetString());

        var adminId = await factory.WithDbAsync(async db => (await db.Users.SingleAsync(u => u.Email == ApiFactory.AdminEmail)).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync($"/api/profile/llm?userId={adminId}")).StatusCode);
        Assert.Equal(1, await factory.WithDbAsync(db => db.UserLlmSettings.CountAsync()));
    }

    [Fact]
    public async Task Settings_UnknownConnectionOrTask_Rejected()
    {
        var (_, b) = await User();

        var unknown = await Choose(b, new { Evaluation = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("nope", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Choose(b, new { Translation = FakeLlmConnections.Fast })).StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.UserLlmSettings.CountAsync()));
    }

    // Конвейер ставит в запрос выбор владельца отклика: у B — своё подключение, у администратора — маршрут сервера
    [Fact]
    public async Task Pipeline_UsesOwnerChoice_PerTask()
    {
        var (bId, b) = await User();
        await Json(await Choose(b, new { Letters = FakeLlmConnections.Fast }));
        foreach (var client in new[] { _admin, b })
            await Json(await client.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#", rules = new { } }));
        factory.Source.Add("1", "C# разработчик", "ASP.NET Core");
        await Json(await _admin.PostAsync("/api/collect", null));
        factory.Llm.Configured = true;
        factory.Llm.Answers.Enqueue("Здравствуйте! Письмо под вакансию без выдуманных фактов.");
        factory.Llm.Answers.Enqueue("Здравствуйте! Письмо под вакансию без выдуманных фактов.");

        await Json(await _admin.PostAsJsonAsync("/api/llm/letters", new { limit = 10 }));

        Assert.Equal(2, factory.Llm.Requests.Count);
        Assert.Equal([null, FakeLlmConnections.Fast], factory.Llm.Requests.Select(r => r.Provider).Order());
        // Оценка у B не выбрана — маршрут сервера
        var choices = await factory.WithDbAsync(db => LlmChoices.LoadAsync(db, [bId], default));
        Assert.Equal((null, FakeLlmConnections.Fast), (choices.For(bId, VacancyAssessment.Purpose), choices.For(bId, CoverLetterWriting.Purpose)));
    }

    // Разовый прогон: отсеянная вакансия сразу оценивается выбранным подключением, а не настройкой пользователя
    [Fact]
    public async Task Reevaluate_WithProvider_EvaluatesNowThroughIt()
    {
        await Json(await Choose(_admin, new { Evaluation = FakeLlmConnections.Fast }));
        await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#", rules = new { } }));
        factory.Source.Add("1", "C# разработчик", "ASP.NET Core");
        await Json(await _admin.PostAsync("/api/collect", null));
        await Json(await _admin.PostAsJsonAsync("/api/vacancies/1/status", new { status = "Skipped", reason = "пропущено вручную" }));
        factory.Llm.Configured = true;
        factory.Llm.Answers.Enqueue("""{"verdict": "fit", "score": 90, "resume": ".NET", "reason": "стек совпадает"}""");

        var result = await Json(await _admin.PostAsync($"/api/vacancies/1/reevaluate?provider={FakeLlmConnections.Giga}", null));

        Assert.Equal(("Queued", "стек совпадает"), (result.GetProperty("status").GetString(), result.GetProperty("llmReason").GetString()));
        var request = Assert.Single(factory.Llm.Requests);
        Assert.Equal((VacancyAssessment.Purpose, FakeLlmConnections.Giga), (request.Purpose, request.Provider));
    }

    [Fact]
    public async Task Reevaluate_UnknownProvider_400_AndChangesNothing()
    {
        await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#", rules = new { } }));
        factory.Source.Add("1", "C# разработчик", "ASP.NET Core");
        await Json(await _admin.PostAsync("/api/collect", null));
        await Json(await _admin.PostAsJsonAsync("/api/vacancies/1/status", new { status = "Skipped", reason = "пропущено вручную" }));
        factory.Llm.Configured = true;

        var response = await _admin.PostAsync("/api/vacancies/1/reevaluate?provider=nope", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Skipped", await factory.WithDbAsync(async db => (await db.JobApplications.SingleAsync()).Status.ToString()));
        Assert.Empty(factory.Llm.Requests);
    }
}
