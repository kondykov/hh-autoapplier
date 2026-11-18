using HhAutoApplier.Domain.Notifications;
using HhAutoApplier.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HhAutoApplier.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        builder.Property(x => x.Key).HasMaxLength(200);
        builder.Property(x => x.Text).HasMaxLength(Notification.MaxTextLength);
        builder.Property(x => x.VacancyId).HasMaxLength(64);
        builder.Property(x => x.Error).HasMaxLength(500);
        // Одно событие — одно сообщение
        builder.HasIndex(x => new { x.UserId, x.Key }).IsUnique();
        // Отправка выбирает неотправленные
        builder.HasIndex(x => x.SentAt).HasFilter("\"SentAt\" IS NULL");
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        // Досылка после сбоя связи (#132) выбирает ждущие
        builder.HasIndex(x => x.WaitingSince).HasFilter("\"WaitingSince\" IS NOT NULL");
        builder.Ignore(x => x.Pending);
        builder.Ignore(x => x.Waiting);
    }
}
