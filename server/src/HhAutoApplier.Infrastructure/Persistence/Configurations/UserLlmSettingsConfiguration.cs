using HhAutoApplier.Domain.Llm;
using HhAutoApplier.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HhAutoApplier.Infrastructure.Persistence.Configurations;

public class UserLlmSettingsConfiguration : IEntityTypeConfiguration<UserLlmSettings>
{
    public void Configure(EntityTypeBuilder<UserLlmSettings> builder)
    {
        builder.ToTable("UserLlmSettings");
        // Одни настройки у пользователя; у владельца по умолчанию (UserId = null) — тоже одни
        builder.HasIndex(x => x.UserId).IsUnique().AreNullsDistinct(false);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        // Выбор по задачам — колонкой jsonb
        builder.OwnsMany(x => x.Choices, o => o.ToJson());
    }
}
