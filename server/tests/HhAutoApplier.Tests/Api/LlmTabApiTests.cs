using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Tests.Api;

// Вкладка LLM (#127): «Обзор», «Отсев» с группами по причине и массовые действия только по своим откликам
[Collection(ApiCollection.Name)]
public class LlmTabApiTests(ApiFactory factory) : IAsyncLifetime
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

    // Три вакансии администратора, отсеянные LLM: стек (пограничная, 65), уровень (40), офис (30)
    private async Task SeedRejected()
    {
        await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#", rules = new { } }));
        factory.Source.Add("1", "PHP-разработчик", "Symfony");
        factory.Source.Add("2", "Senior .NET", "ASP.NET Core");
        factory.Source.Add("3", ".NET в офисе", "ASP.NET Core");
        await Json(await _admin.PostAsync("/api/collect", null));
        await Reject("1", 65, "основной стек PHP, в профиле C#");
        await Reject("2", 40, "нужен Senior с опытом от 6 лет");
        await Reject("3", 30, "только офис в Москве, переезд");
    }

    private Task Reject(string id, int score, string reason) => factory.WithDbAsync(async db =>
    {
        var why = $"LLM: {reason}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "JobApplications" SET "LlmVerdict" = 'Reject', "LlmScore" = {score}, "Score" = {score}, "LlmReason" = {reason}, "LlmModel" = 'fake', "LlmEvaluatedAt" = now(), "Status" = 'Skipped', "StatusReason" = {why} WHERE "VacancyId" = {id}""");
        return 0;
    });

    private async Task<JsonElement> Rejects(string query = "", HttpClient? client = null) =>
        await Json(await (client ?? _admin).GetAsync($"/api/llm/rejects?period=30{query}"));

    private static int Group(JsonElement rejects, string id) =>
        rejects.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("id").GetString() == id).GetProperty("count").GetInt32();

    private static List<string?> Ids(JsonElement rejects) =>
        rejects.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();

    private Task<JsonElement> Bulk(string action, string[] ids, HttpClient? client = null) =>
        Task.Run(async () => await Json(await (client ?? _admin).PostAsJsonAsync("/api/vacancies/bulk", new { action, ids })));

    private Task<string> Status(string id) =>
        factory.WithDbAsync(async db => (await db.JobApplications.SingleAsync(a => a.VacancyId == id)).Status.ToString());

    [Fact]
    public async Task Rejects_GroupedByReason_BorderlineFilter()
    {
        await SeedRejected();

        var all = await Rejects();
        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(1, all.GetProperty("borderline").GetInt32());
        Assert.Equal((1, 1, 1, 0), (Group(all, "stack"), Group(all, "level"), Group(all, "office"), Group(all, "other")));
        Assert.Equal(["1", "2", "3"], Ids(all));
        Assert.Equal("stack", all.GetProperty("items")[0].GetProperty("group").GetString());

        Assert.Equal(["2"], Ids(await Rejects("&group=level")));
        var border = await Rejects("&border=true");
        Assert.Equal(["1"], Ids(border));
        Assert.Equal(1, border.GetProperty("matched").GetInt32());
    }

    // Массовые действия — только по своим: чужие id не найдены и не меняются; «Вернуть» отменяется Skip с причиной LLM
    [Fact]
    public async Task Bulk_OnlyOwn_RequeueUndoHide()
    {
        await SeedRejected();
        var (_, b) = await User();

        var foreign = await Bulk("Requeue", ["1", "2"], b);
        Assert.Empty(foreign.GetProperty("done").EnumerateArray());
        Assert.Equal(2, foreign.GetProperty("failed").GetArrayLength());
        Assert.Equal("Skipped", await Status("1"));
        Assert.Equal(0, (await Rejects(client: b)).GetProperty("total").GetInt32());

        var requeued = await Bulk("Requeue", ["1", "2", "missing"]);
        Assert.Equal(2, requeued.GetProperty("done").GetArrayLength());
        Assert.Equal("missing", requeued.GetProperty("failed")[0].GetProperty("id").GetString());
        Assert.Equal("Queued", await Status("1"));
        Assert.Equal(1, (await Rejects()).GetProperty("total").GetInt32());
        var overview = await Json(await _admin.GetAsync("/api/llm/overview"));
        Assert.Equal(2, overview.GetProperty("rejects").GetProperty("returned").GetInt32());

        await Bulk("Skip", ["1"]);
        Assert.Equal("Skipped", await Status("1"));
        Assert.Equal("LLM: основной стек PHP, в профиле C#",
            await factory.WithDbAsync(async db => (await db.JobApplications.SingleAsync(a => a.VacancyId == "1")).StatusReason));

        await Bulk("Hide", ["1", "3"]);
        Assert.Equal(0, (await Rejects()).GetProperty("total").GetInt32());
        await Bulk("Unhide", ["3"]);
        Assert.Equal(["3"], Ids(await Rejects()));
    }

    [Fact]
    public async Task Overview_OwnData_AdminSeesUsersAndOthers()
    {
        await SeedRejected();
        var (bId, b) = await User();

        var ofB = await Json(await b.GetAsync("/api/llm/overview"));
        Assert.Equal(JsonValueKind.Null, ofB.GetProperty("requests").GetProperty("byUser").ValueKind);
        Assert.Equal(0, ofB.GetProperty("rejects").GetProperty("rejected").GetInt32());
        Assert.Equal(4, ofB.GetProperty("routes").GetArrayLength());

        var adminId = await factory.WithDbAsync(async db => (await db.Users.SingleAsync(u => u.Email == ApiFactory.AdminEmail)).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync($"/api/llm/overview?userId={adminId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync($"/api/llm/rejects?userId={adminId}")).StatusCode);

        var admin = await Json(await _admin.GetAsync("/api/llm/overview"));
        Assert.NotEqual(JsonValueKind.Null, admin.GetProperty("requests").GetProperty("byUser").ValueKind);
        Assert.Equal(3, admin.GetProperty("rejects").GetProperty("rejected").GetInt32());
        Assert.Contains(admin.GetProperty("attention").EnumerateArray(), a => a.GetProperty("kind").GetString() == "rejects");
        var adminOfB = await Json(await _admin.GetAsync($"/api/llm/overview?userId={bId}"));
        Assert.Equal(0, adminOfB.GetProperty("rejects").GetProperty("rejected").GetInt32());
        Assert.Equal(3, (await Rejects($"&userId={adminId}")).GetProperty("total").GetInt32());
    }
}
