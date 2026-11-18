using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Notifications;

// Досылка после сбоя связи (#132): важные — снова в outbox и уходят по одному, как обычно; обычные (строки сводки) —
// одним сообщением «пока не было связи» на пользователя; старше суток — «устарело», не слать. Всё — одним сохранением:
// после перезапуска сервера досланное не ждёт снова, а сводка не повторяется (ключ уведомления уникален)
public class ResumeWaitingNotificationsCommandHandler(IAppDbContext db, TimeProvider clock)
    : ICommandHandler<ResumeWaitingNotificationsCommand, ResumeWaitingNotificationsResult>
{
    public async Task<ResumeWaitingNotificationsResult> Handle(ResumeWaitingNotificationsCommand command, CancellationToken ct = default)
    {
        // Ждущие с момента после успешной проверки — до следующей: связь могла пропасть снова
        var waiting = await db.Notifications.Where(n => n.SentAt == null && n.WaitingSince != null && n.WaitingSince <= command.TelegramOkAt)
            .OrderBy(n => n.Id).ToListAsync(ct);
        if (waiting.Count == 0)
            return new ResumeWaitingNotificationsResult(0, 0, 0);

        var now = clock.GetUtcNow();
        int resumed = 0, digested = 0, expired = 0;
        var digests = new Dictionary<Guid, List<Notification>>();
        foreach (var notification in waiting)
        {
            if (notification.Stale(now))
            {
                notification.Resume(now);
                expired++;
            }
            else if (BotTexts.Delivery(notification.Kind) == NotificationDelivery.Digest)
            {
                if (!digests.TryGetValue(notification.UserId, out var lines))
                    digests[notification.UserId] = lines = [];
                lines.Add(notification);
            }
            else if (notification.Resume(now))
                resumed++;
        }

        foreach (var (userId, missed) in digests)
        {
            foreach (var notification in missed)
                notification.CoverByDigest();
            digested += missed.Count;
            db.Notifications.Add(Notification.Create(userId, NotificationKind.MissedDigest, $"missed:{missed[0].Id}",
                BotTexts.MissedDigest(missed.Select(n => (n.CreatedAt, n.Text))), now));
        }

        await db.SaveChangesAsync(ct);
        return new ResumeWaitingNotificationsResult(resumed, digested, expired);
    }
}
