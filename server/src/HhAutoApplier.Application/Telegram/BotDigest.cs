using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Domain.Dialogs;
using HhAutoApplier.Domain.Vacancies;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Telegram;

// Счётчики для меню и кнопок сводки уведомлений (#115): «Проверка (5)», «Ответы (2)» — по необходимости
public class BotDigest(IAppDbContext db, ICurrentUser current)
{
    // Необработанные ответы работодателей, кроме отказов — как список «Ответы» в вебе
    public Task<int> OpenDialogsAsync(CancellationToken ct) => OpenDialogsAsync(current.Id, ct);

    private Task<int> OpenDialogsAsync(Guid? userId, CancellationToken ct) =>
        db.EmployerMessages.CountAsync(m => m.UserId == userId && m.HandledAt == null && m.Kind != DialogKind.Rejection, ct);

    // Кнопки сводки пользователя: каждая открывает раздел заново внизу чата
    public async Task<IReadOnlyList<IReadOnlyList<BotButton>>> ButtonsAsync(Guid userId, CancellationToken ct)
    {
        var review = await db.JobApplications.CountAsync(a => a.UserId == userId && a.Status == VacancyStatus.Review, ct);
        var dialogs = await OpenDialogsAsync(userId, ct);
        var buttons = new List<BotButton>();
        if (review > 0)
            buttons.Add(new BotButton($"📝 Проверка ({review})", $"{BotTexts.Open}:{ReviewSection.Card}:0"));
        if (dialogs > 0)
            buttons.Add(new BotButton($"💬 Ответы ({dialogs})", $"{BotTexts.Open}:{DialogsSection.List}:0:o"));
        buttons.Add(new BotButton("☰ Меню", $"{BotTexts.Open}:{MenuSection.Show}:"));
        return [buttons];
    }
}
