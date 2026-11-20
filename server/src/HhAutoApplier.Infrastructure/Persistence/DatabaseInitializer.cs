using HhAutoApplier.Application.Commands.Dialogs;
using HhAutoApplier.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HhAutoApplier.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    // Миграции, начальные данные и разовые пересчёты данных при старте приложения
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync(ct);
        await DbSeeder.SeedAsync(db, ct);
        await IdentitySeeder.SeedAsync(scope.ServiceProvider, ct);
        // Собеседования, пришедшие до статуса Interview (#122): идемпотентно, повторный запуск ничего не меняет
        await scope.ServiceProvider.GetRequiredService<RecountInterviewsCommandHandler>().Handle(new RecountInterviewsCommand(), ct);
    }
}