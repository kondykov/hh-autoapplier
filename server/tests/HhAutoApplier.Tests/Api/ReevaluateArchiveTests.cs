using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Tests.Api;

// Переоценка сначала проверяет, не ушла ли вакансия в архив (#147): архивная и недоступная не переоцениваются,
// свежая проверка не повторяется, hh.ru не ответил — переоценки нет
[Collection(ApiCollection.Name)]
public class ReevaluateArchiveTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Search = "dotnet";

    private const string Archived = "вакансия в архиве";

    private const string Unavailable = "вакансия недоступна";

    private readonly HttpClient _api = factory.CreateApiClient();

    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Отсеянные вручную отклики, вакансии давно не встречались в выдаче; LLM настроена, DetailsLoaded считает только проверки
    private async Task Dismissed(params string[] ids)
    {
        await Json(await _api.PutAsJsonAsync("/api/searches", new { name = Search, query = "C#", rules = new { } }));
        foreach (var id in ids)
            factory.Source.Add(id, $"C# разработчик {id}", "ASP.NET Core");

        await Json(await _api.PostAsync("/api/collect", null));
        foreach (var id in ids)
            await Json(await _api.PostAsJsonAsync($"/api/vacancies/{id}/status", new { status = "Skipped", reason = "пропущено вручную" }));

        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""UPDATE "Vacancies" SET "LastSeenAt" = now() - interval '25 hours'"""));
        factory.Llm.Configured = true;
        factory.Source.ResetCounters();
    }

    private Task<HttpResponseMessage> Reevaluate(string id) => _api.PostAsync($"/api/vacancies/{id}/reevaluate", null);

    private async Task<(string? Status, string? Reason)> State(string id)
    {
        var v = (await Json(await _api.GetAsync("/api/vacancies?take=200&includeHidden=true"))).EnumerateArray()
            .Single(v => v.GetProperty("id").GetString() == id);
        return (v.GetProperty("status").GetString(), v.GetProperty("statusReason").GetString());
    }

    private Task<DateTimeOffset?> ArchiveChecked(string id) =>
        factory.WithDbAsync(db => db.Vacancies.Where(v => v.Id == id).Select(v => v.ArchiveCheckedAt).SingleAsync());

    private static List<string> Ids(JsonElement result, string name) =>
        result.GetProperty(name).EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Archived_IsNotReevaluated_StaysSkippedWithReason()
    {
        await Dismissed("1");
        factory.Source.ArchivedPages.Add("1");

        var result = await Json(await Reevaluate("1"));

        Assert.Equal(("Skipped", Archived), (result.GetProperty("status").GetString(), result.GetProperty("statusReason").GetString()));
        Assert.DoesNotContain(result.GetProperty("history").EnumerateArray(), e => e.GetProperty("reason").GetString() == "повторная оценка");
        Assert.Equal(1, factory.Source.DetailsLoaded);
        Assert.NotNull(await ArchiveChecked("1"));
        Assert.Empty(factory.Llm.Requests);
    }

    [Fact]
    public async Task Unavailable_IsNotReevaluated()
    {
        await Dismissed("1");
        factory.Source.NoDetails.Add("1");

        var result = await Json(await Reevaluate("1"));

        Assert.Equal(("Skipped", Unavailable), (result.GetProperty("status").GetString(), result.GetProperty("statusReason").GetString()));
    }

    [Fact]
    public async Task FreshCheck_IsNotRepeated_AndRemembersArchive()
    {
        await Dismissed("1");
        factory.Source.ArchivedPages.Add("1");
        await Json(await Reevaluate("1"));

        var again = await Json(await Reevaluate("1"));

        Assert.Equal(1, factory.Source.DetailsLoaded);
        Assert.Equal("Skipped", again.GetProperty("status").GetString());
        Assert.Equal(Archived, again.GetProperty("statusReason").GetString());
    }

    [Fact]
    public async Task Available_IsReevaluated_AfterCheck()
    {
        await Dismissed("1");

        var result = await Json(await Reevaluate("1"));

        Assert.Equal("Pending", result.GetProperty("status").GetString());
        Assert.Equal(1, factory.Source.DetailsLoaded);
        Assert.NotNull(await ArchiveChecked("1"));
    }

    [Fact]
    public async Task RecentlySeenInSearch_IsReevaluated_WithoutRequest()
    {
        await Dismissed("1");
        await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""UPDATE "Vacancies" SET "LastSeenAt" = now()"""));

        var result = await Json(await Reevaluate("1"));

        Assert.Equal("Pending", result.GetProperty("status").GetString());
        Assert.Equal(0, factory.Source.DetailsLoaded);
    }

    [Fact]
    public async Task SourceBlocked_Returns502_AndChangesNothing()
    {
        await Dismissed("1");
        factory.Source.BlockedDetails.Add("1");

        var response = await Reevaluate("1");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("переоценка не выполнена", await response.Content.ReadAsStringAsync());
        Assert.Equal(("Skipped", "пропущено вручную"), await State("1"));
        Assert.Null(await ArchiveChecked("1"));
    }

    [Fact]
    public async Task SourceError_Returns502_AndChangesNothing()
    {
        await Dismissed("1");
        factory.Source.ErrorPages.Add("1");

        var response = await Reevaluate("1");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(("Skipped", "пропущено вручную"), await State("1"));
    }

    [Fact]
    public async Task NotDismissed_IsConflict_WithoutRequest()
    {
        await Dismissed("1");
        await Json(await _api.PostAsJsonAsync("/api/vacancies/1/status", new { status = "Queued" }));

        var response = await Reevaluate("1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, factory.Source.DetailsLoaded);
    }

    [Fact]
    public async Task Bulk_CountsArchivedAndUnavailable()
    {
        await Dismissed("1", "2", "3");
        factory.Source.ArchivedPages.Add("1");
        factory.Source.NoDetails.Add("2");

        var result = await Json(await _api.PostAsJsonAsync("/api/vacancies/bulk", new { action = "Reevaluate", ids = new[] { "1", "2", "3", "404" } }));

        Assert.Equal(["3"], Ids(result, "done"));
        Assert.Equal(["1"], Ids(result, "archived"));
        Assert.Equal(["2"], Ids(result, "unavailable"));
        Assert.Equal(["404"], Ids(result, "failed"));
        Assert.Equal(("Pending", "Skipped", "Skipped"), ((await State("3")).Status, (await State("1")).Status, (await State("2")).Status));
    }

    [Fact]
    public async Task Bulk_StopsAskingSourceAfterBlock()
    {
        await Dismissed("1", "2");
        factory.Source.BlockedDetails.Add("1");

        var result = await Json(await _api.PostAsJsonAsync("/api/vacancies/bulk", new { action = "Reevaluate", ids = new[] { "1", "2" } }));

        Assert.Equal(["1", "2"], Ids(result, "failed"));
        Assert.Empty(Ids(result, "done"));
        Assert.Equal(1, factory.Source.DetailsLoaded);
        Assert.Equal(("Skipped", "пропущено вручную"), await State("2"));
    }
}
