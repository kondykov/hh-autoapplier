using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Domain.Dialogs;
using HhAutoApplier.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Notifications;

public record NotifyEmployerLinksCommand;

public class NotifyEmployerLinksCommandHandler(IAppDbContext db, Notifier notifier, ILlmClient llm, TimeProvider clock)
    : ICommandHandler<NotifyEmployerLinksCommand, int>
{
    public async Task<int> Handle(NotifyEmployerLinksCommand command, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        // Даём разбору один проход. Без ответа модели используем только известные домены.
        var fallbackBefore = llm.IsConfigured ? now.AddMinutes(-2) : now;
        var linked = db.TelegramChats.Where(chat => chat.ChatId != null).Select(chat => chat.UserId);
        var messages = await db.EmployerMessages.Where(message => linked.Contains(message.UserId)
            && message.ExternalLinks.Any(link => link.State == EmployerLinkState.Pending && link.RequiresAction != false
                && ((link.NotifiedAt == null && (link.RequiresAction == true || link.DetectedAt <= fallbackBefore))
                    || (link.RemindedAt == null && link.RemindAt <= now)))).ToListAsync(ct);

        var notified = 0;
        foreach (var message in messages)
        foreach (var link in message.ExternalLinks.Where(link => link.State == EmployerLinkState.Pending && link.RequiresAction != false))
        {
            var key = $"ext:{message.ChatId}:{link.Key}";
            if (link.NotifiedAt is null && (link.RequiresAction == true || link.DetectedAt <= fallbackBefore))
            {
                if (await NotifyAsync(message, link, key, false, ct))
                {
                    link.MarkNotified(now);
                    notified++;
                }
            }
            if (link.NotifiedAt.HasValue && link.RemindedAt is null && link.RemindAt <= now
                && await NotifyAsync(message, link, $"{key}:reminder", true, ct))
            {
                link.MarkReminded(now);
                notified++;
            }
        }
        await db.SaveChangesAsync(ct);
        return notified;
    }

    private async Task<bool> NotifyAsync(EmployerMessage message, EmployerLink link, string key, bool reminder, CancellationToken ct)
    {
        var type = link.Kind == EmployerLinkKind.Form ? "анкета" : "тест";
        var text = $"{(reminder ? "Напоминание" : "Ссылка вне hh.ru")}: {type}\n{message.Title}\n{message.Company}\n{link.Domain}\n" +
            (link.Action is { } action ? $"{action}\n" : "") +
            (link.DueAt is { } due ? $"До {MoscowTime.Format(due)}\n" : "") +
            $"{link.Quote}\n{link.Url}";
        return await notifier.NotifyAsync(message.UserId, NotificationKind.ExternalLink, key, text, message.VacancyId, ct)
            || await db.Notifications.AnyAsync(notification => notification.UserId == message.UserId && notification.Key == key, ct);
    }
}
