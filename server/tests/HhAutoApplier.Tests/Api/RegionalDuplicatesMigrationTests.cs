using HhAutoApplier.Domain.Vacancies;
using HhAutoApplier.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace HhAutoApplier.Tests.Api;

[Collection(ApiCollection.Name)]
public class RegionalDuplicatesMigrationTests(ApiFactory factory)
{
    [Fact]
    public async Task UpgradeGroupsOldQueueAndPreservesSentApplications()
    {
        const string previous = "20261120184348_ResponsesCount";
        var connection = await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""CREATE DATABASE "migration_143" """);
            return db.Database.GetConnectionString()!;
        });
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = "migration_143" }.ConnectionString).Options;
        await using var db = new AppDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Vacancies" ("Id", "Title", "Company", "Url", "Area", "Description", "WorkFormat", "Stack", "HasTest",
                "PublishedAt", "CollectedAt", "LastSeenAt", "SearchName", "Imported")
            SELECT id, 'Backend', 'Acme', 'https://hh.ru/vacancy/' || id, region, 'C# ASP.NET', 'Удалённо', ARRAY[]::text[], false,
                '2026-11-21T00:00:00Z', '2026-11-21T00:00:00Z', '2026-11-21T00:00:00Z', 'test', false
            FROM (VALUES ('1', 'Тверь'), ('2', 'Москва'), ('3', 'Казань')) AS sample(id, region)
            """);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "JobApplications" ("VacancyId", "CreatedAt", "Status", "Score", "AppliedAt", "LetterRequestsState", "History")
            VALUES
                ('1', '2026-11-21T09:00Z', 'Applied', 0, '2026-11-21T09:30Z', 'NotExtracted', '[]'),
                ('2', '2026-11-22T09:00Z', 'Queued', 0, null, 'NotExtracted', '[]'),
                ('3', '2026-11-23T09:00Z', 'Applied', 0, '2026-11-23T09:30Z', 'NotExtracted', '[]')
            """);
        await migrator.MigrateAsync();
        var apps = await db.JobApplications.AsNoTracking().OrderBy(a => a.VacancyId).ToArrayAsync();
        Assert.Equal(VacancyStatus.Applied, apps[0].Status);
        Assert.Null(apps[0].DuplicateOfVacancyId);
        Assert.Equal(VacancyStatus.Skipped, apps[1].Status);
        Assert.Equal("1", apps[1].DuplicateOfVacancyId);
        Assert.Equal(VacancyStatus.Applied, apps[2].Status);
        Assert.Null(apps[2].DuplicateOfVacancyId);
        Assert.Equal(VacancyStatus.Queued, Assert.Single(apps[1].History).From);

        await migrator.MigrateAsync(previous);
        var statusAfterRollback = await db.Database
            .SqlQueryRaw<string>("""SELECT "Status" AS "Value" FROM "JobApplications" WHERE "VacancyId" = '2'""").SingleAsync();
        Assert.Equal("Queued", statusAfterRollback);
    }
}
