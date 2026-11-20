using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HhAutoApplier.Application.Commands.Dialogs;
using HhAutoApplier.Domain.Dialogs;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Tests.Api;

// Дашборд (#121) и собеседования (#122): права, воронка по данным из базы, пересчёт собеседований по сохранённым сообщениям
[Collection(ApiCollection.Name)]
public class DashboardApiTests(ApiFactory factory) : IAsyncLifetime
{
    private const string Password = "user-password-1";

    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddDays(-1);

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

    private Task<Guid> AdminId() => factory.WithDbAsync(async db => (await db.Users.SingleAsync(u => u.Email == ApiFactory.AdminEmail)).Id);

    // Отклики администратора на вакансии 1–3 отправлены
    private async Task Applied()
    {
        await Json(await _admin.PutAsJsonAsync("/api/searches", new { name = "dotnet", query = "C#" }));
        object Card(string id) => new { id, title = $"C# {id}", company = "Acme", description = "", salaryCurrency = "RUR", workFormat = "Удалённо" };
        await Json(await _admin.PostAsJsonAsync("/api/import", new { search = "dotnet", vacancies = new[] { Card("1"), Card("2"), Card("3") } }));
        await factory.ConfirmImportedAsync();
        foreach (var id in new[] { "1", "2", "3" })
            await Json(await _admin.PostAsJsonAsync($"/api/vacancies/{id}/status", new { status = "Applied" }));
    }

    private static object Chat(long chatId, string vacancyId, string state, long messageId) =>
        new { chatId, vacancyId, title = $"C# {vacancyId}", company = "Acme", state, messageId, text = "Здравствуйте!", at = At, fromEmployer = true };

    private static int Step(JsonElement dashboard, string key) =>
        dashboard.GetProperty("funnel").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key).GetProperty("count").GetInt32();

    [Fact]
    public async Task Dashboard_FunnelWithInterview_AndChatsSync()
    {
        await Applied();
        await Json(await _admin.PostAsJsonAsync("/api/dialogs", new { chats = new[] { Chat(1, "1", "INVITATION", 11), Chat(2, "2", "INTERVIEW", 12), Chat(3, "3", "DISCARD", 13) } }));

        var d = await Json(await _admin.GetAsync("/api/dashboard?period=7"));

        Assert.Equal((3, 3, 2, 1, 1), (Step(d, "collected"), Step(d, "sent"), Step(d, "invited"), Step(d, "interview"), Step(d, "rejected")));
        var totals = d.GetProperty("totals");
        Assert.Equal((3, 2, 1, 1), (totals.GetProperty("sent").GetInt32(), totals.GetProperty("invited").GetInt32(),
            totals.GetProperty("interviews").GetInt32(), totals.GetProperty("rejected").GetInt32()));
        Assert.Equal(7, d.GetProperty("series").GetProperty("points").GetArrayLength());
        Assert.Equal("dotnet", d.GetProperty("bySearch")[0].GetProperty("name").GetString());
        Assert.Equal("hh", d.GetProperty("sources")[0].GetProperty("source").GetString());
        var chats = d.GetProperty("chats");
        Assert.Equal(3, chats.GetProperty("messages").GetInt32());
        Assert.True(chats.GetProperty("syncedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/api/dashboard?period=14")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_UserSeesOwn_AdminAnyone_SystemOnlyAdmin()
    {
        await Applied();
        var (userId, user) = await User();

        var own = await Json(await user.GetAsync("/api/dashboard"));
        Assert.Equal(0, Step(own, "collected"));
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/api/dashboard?userId={await AdminId()}")).StatusCode);
        Assert.Equal(0, Step(await Json(await _admin.GetAsync($"/api/dashboard?userId={userId}")), "collected"));
        Assert.Equal(3, Step(await Json(await _admin.GetAsync("/api/dashboard")), "collected"));

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/dashboard/system")).StatusCode);
        var system = await Json(await _admin.GetAsync("/api/dashboard/system"));
        var admin = system.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("email").GetString() == ApiFactory.AdminEmail);
        Assert.Equal((3, 3), (admin.GetProperty("collected").GetInt32(), admin.GetProperty("sent").GetInt32()));
        Assert.Equal(2, system.GetProperty("users").GetArrayLength());
        Assert.Equal(0, system.GetProperty("notifications").GetProperty("unsent").GetInt32());
        Assert.False(string.IsNullOrEmpty(system.GetProperty("version").GetProperty("version").GetString()));
        Assert.True(system.GetProperty("llm").TryGetProperty("routes", out _));
    }

    // Собеседования до #122 записаны как Invited: пересчёт по сохранённым сообщениям INTERVIEW — один раз, повтор ничего не меняет
    [Fact]
    public async Task RecountInterviews_FromStoredMessages_Idempotent()
    {
        await Applied();
        var admin = await AdminId();
        var old = DateTimeOffset.UtcNow.AddDays(-3);
        await factory.WithDbAsync(async db =>
        {
            // Как было до #122: INTERVIEW → Invited; второй — отказ после собеседования; третий — ждёт ответа без сообщения
            var invited = await db.JobApplications.SingleAsync(a => a.VacancyId == "1");
            invited.Invite("приглашение (чат hh.ru)", old);
            var rejected = await db.JobApplications.SingleAsync(a => a.VacancyId == "2");
            rejected.Reject("работодатель отказал", old);
            db.EmployerMessages.Add(EmployerMessage.Create(admin, 1, "1", "C# 1", "Acme", 11, "Ждём на собеседование", old, "INTERVIEW", old, false));
            db.EmployerMessages.Add(EmployerMessage.Create(admin, 2, "2", "C# 2", "Acme", 12, "Ждём на собеседование", old, "INTERVIEW", old, false));
            return await db.SaveChangesAsync();
        });

        async Task<int> Recount()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<RecountInterviewsCommandHandler>().Handle(new RecountInterviewsCommand());
        }

        Assert.Equal(1, await Recount());
        Assert.Equal(0, await Recount());

        var states = await factory.WithDbAsync(db => db.JobApplications.AsNoTracking().OrderBy(a => a.VacancyId).ToListAsync());
        Assert.Equal([VacancyStatus.Interview, VacancyStatus.Rejected, VacancyStatus.Applied], states.Select(a => a.Status));
        var last = states[0].History[^1];
        Assert.Equal((VacancyStatus.Invited, VacancyStatus.Interview, RecountInterviewsCommandHandler.Reason), (last.From!.Value, last.To, last.Reason));
        Assert.Single(states[0].History, e => e.To == VacancyStatus.Interview);
    }
}
