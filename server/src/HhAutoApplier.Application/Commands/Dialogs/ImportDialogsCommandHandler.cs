using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Errors;
using HhAutoApplier.Domain.Dialogs;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Dialogs;

// Сообщения работодателей — в EmployerMessages (разбор LLM потом, в фоне), состояние hh.ru — в статус отклика:
// отказ → Rejected, приглашение → Invited, собеседование → Interview (#122). Чат по вакансии есть только после отклика: отклик, чей статус
// потерялся (Failed, Queued…), становится Applied (2026-10-27 так потерялся 137435290). В ответе — чаты, где ждёт автоответ на отказ.
// Первый сбор пользователя только запоминает старые отказы: автоответ — на отказы, пришедшие после него
public class ImportDialogsCommandHandler(IAppDbContext db, ICurrentUser current, TimeProvider clock)
    : ICommandHandler<ImportDialogsCommand, ImportDialogsResult>
{
    public const int MaxAutoReplies = 20;

    public const string InterviewReason = "собеседование (чат hh.ru)";

    public async Task<ImportDialogsResult> Handle(ImportDialogsCommand command, CancellationToken ct = default)
    {
        if (current.Id is not { } userId)
            return new ConflictError("Чаты принимаются только от пользователя: войдите или задайте токен");

        var now = clock.GetUtcNow();
        // Чат мог прийти дважды (страницы списка сдвинулись) — берётся последний
        var chats = command.Chats.Where(c => c.ChatId > 0).GroupBy(c => c.ChatId).Select(g => g.Last()).ToList();
        var chatIds = chats.Select(c => c.ChatId).ToList();
        var vacancyIds = chats.Select(c => c.VacancyId).OfType<string>().Distinct().ToList();

        // Начало первого сбора: отказы старше него — без автоответа (и чаты, что в первый сбор не попали)
        var since = await db.EmployerMessages.Where(m => m.UserId == userId).MinAsync(m => (DateTimeOffset?)m.ReceivedAt, ct) ?? now;
        var firstSync = since == now;
        var known = await db.EmployerMessages.Where(m => m.UserId == userId && chatIds.Contains(m.ChatId)).ToDictionaryAsync(m => m.ChatId, ct);
        var applications = await db.JobApplications.Where(a => a.UserId == userId && vacancyIds.Contains(a.VacancyId)).ToDictionaryAsync(a => a.VacancyId, ct);

        int newMessages = 0, rejected = 0, invited = 0, interviews = 0, recovered = 0, externalLinks = 0;
        foreach (var chat in chats)
        {
            if (chat.VacancyId is { } vacancyId && applications.GetValueOrDefault(vacancyId) is { } application)
            {
                if (!application.Sent)
                {
                    application.RecoverApplied(now);
                    recovered++;
                }

                if (chat.State == EmployerMessage.Rejected && application.Status is VacancyStatus.Applied or VacancyStatus.Invited or VacancyStatus.Interview)
                {
                    application.Reject("работодатель отказал (чат hh.ru)", now);
                    rejected++;
                }
                else if (chat.State == EmployerMessage.Interview && application.Status is VacancyStatus.Applied or VacancyStatus.Invited)
                {
                    application.ScheduleInterview(InterviewReason, now);
                    interviews++;
                }
                else if (chat.State == EmployerMessage.Invitation && application.Status == VacancyStatus.Applied)
                {
                    application.Invite("приглашение (чат hh.ru)", now);
                    invited++;
                }
            }

            var message = known.GetValueOrDefault(chat.ChatId);
            if (chat.FromEmployer && !string.IsNullOrWhiteSpace(chat.Text))
            {
                if (message is null)
                {
                    message = EmployerMessage.Create(userId, chat.ChatId, chat.VacancyId, chat.Title, chat.Company,
                        chat.MessageId, chat.Text, chat.At, chat.State, now, autoReply: !firstSync && chat.At >= since);
                    if (!firstSync && chat.At >= since)
                        externalLinks += message.RecordExternalLinks(now);
                    db.EmployerMessages.Add(message);
                    newMessages++;
                }
                else if (message.Receive(chat.VacancyId, chat.Title, chat.Company, chat.MessageId, chat.Text, chat.At, chat.State, now, autoReply: chat.At >= since))
                {
                    if (chat.At >= since)
                        externalLinks += message.RecordExternalLinks(now);
                    newMessages++;
                }
            }
            else if (message is not null && chat.MessageId != message.MessageId)
            {
                // После сообщения работодателя написал сам пользователь
                message.UserReplied(now);
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Те же чаты успел записать другой запрос, например из второй вкладки (уникальный индекс, xmin отклика)
            return new ConflictError("Те же чаты одновременно записал другой запрос — повторите");
        }

        var autoReplies = await db.EmployerMessages.AsNoTracking()
            .Where(m => m.UserId == userId && m.AutoReply == AutoReplyState.Pending)
            .OrderBy(m => m.At).Take(MaxAutoReplies)
            .Select(m => new AutoReplyDto(m.ChatId, m.VacancyId, m.Title, m.Company, m.MessageId))
            .ToListAsync(ct);
        return new ImportDialogsResultDto(chats.Count, newMessages, rejected, invited, recovered, autoReplies, interviews, externalLinks);
    }
}
