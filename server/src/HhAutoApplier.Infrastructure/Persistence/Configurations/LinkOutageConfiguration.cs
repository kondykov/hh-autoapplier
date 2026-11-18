using HhAutoApplier.Domain.Links;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HhAutoApplier.Infrastructure.Persistence.Configurations;

public class LinkOutageConfiguration : IEntityTypeConfiguration<LinkOutage>
{
    public void Configure(EntityTypeBuilder<LinkOutage> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Subject).HasMaxLength(200);
        builder.Property(x => x.Scope).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Reason).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Detail).HasMaxLength(LinkOutage.MaxDetailLength);
        // Каждая проверка читает открытые сбои
        builder.HasIndex(x => x.EndedAt).HasFilter("\"EndedAt\" IS NULL");
        builder.HasIndex(x => x.StartedAt);
        builder.Ignore(x => x.Open);
    }
}
