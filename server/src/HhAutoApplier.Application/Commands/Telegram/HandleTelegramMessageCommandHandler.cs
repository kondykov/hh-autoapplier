using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;
using HhAutoApplier.Domain.Telegram;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Telegram;

// Команды бота: /start <код> привязывает чат, остальные работают только в привязанном чате незаблокированного пользователя.
// Ответ на просьбу бота (правка письма, причина) — по первой строке сообщения бота, на которое ответили (BotPrompt)
public class HandleTelegramMessageCommandHandler(
    IAppDbContext db,
    IUserService users,
    BotChat bot,
    OverviewSection overview,
    ReviewSection review,
    VacanciesSection vacancies,
    DialogsSection dialogs,
    SearchesSection searches,
    CollectionSection collection,
    TimeProvider clock
) : ICommandHandler<HandleTelegramMessageCommand, IReadOnlyList<BotMessage>>
{
    public async Task<IReadOnlyList<BotMessage>> Handle(HandleTelegramMessageCommand command, CancellationToken ct = default)
    {
        if (!command.IsPrivate)
            return [];

        var parts = command.Text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // В группах Telegram дописывает имя бота: /status@my_bot. Кнопка меню присылает свою подпись
        var name = command.Text.Trim() switch
        {
            BotTexts.OverviewButton or BotTexts.OldStatusButton => "/overview",
            BotTexts.ReviewButton or BotTexts.OldReviewButton => "/review",
            BotTexts.DialogsButton => "/dialogs",
            BotTexts.VacanciesButton => "/vacancies",
            BotTexts.SearchesButton => "/searches",
            BotTexts.HelpButton => "/help",
            _ => parts.Length > 0 ? parts[0].Split('@')[0].ToLowerInvariant() : "",
        };
        var argument = parts.Length > 1 ? parts[1] : "";

        if (name == "/start" && argument != "")
            return [await LinkAsync(command, argument, ct)];

        var (state, _) = await bot.EnterAsync(command.ChatId, ct);
        if (state == BotEntryState.NotLinked)
            return [new BotMessage(BotTexts.NotLinked)];
        if (state == BotEntryState.Blocked)
            return [new BotMessage("Учётная запись заблокирована")];

        // Ответ текстом на просьбу бота; команда в ответе — всё равно команда
        if (!name.StartsWith('/') && BotPrompt.Parse(command.ReplyTo) is { } prompt)
            return [await ReplyAsync(prompt.Kind, prompt.VacancyId, command.Text, command.ReplyTo!, ct)];

        switch (name)
        {
            case "/overview" or "/status":
                return [await overview.ScreenAsync(ct)];
            case "/review":
                return [await review.CardAsync(null, 0, ct)];
            case "/dialogs":
                return [await dialogs.ListAsync(0, false, ct)];
            case "/vacancies":
                return [await vacancies.MenuAsync(ct)];
            case "/searches":
                return [await searches.ScreenAsync(ct)];
            case "/collect":
                return [await collection.ScreenAsync(ct)];
            case "/report":
                return [await overview.ReportAsync(ct)];
            case "/stop":
                // Отвязка — кнопкой подтверждения (HandleTelegramCallbackCommandHandler): привязать обратно можно только новым кодом
                return [BotTexts.UnlinkConfirm()];
            default:
                return [new BotMessage(BotTexts.Help) { Menu = BotMenu.Show }];
        }
    }

    private Task<BotMessage> ReplyAsync(string kind, string vacancyId, string text, BotSource source, CancellationToken ct) =>
        kind == BotPrompt.SkipReason
            ? vacancies.SkipAsync(vacancyId, text.Trim(), ct)
            : review.ReplyAsync(kind, vacancyId, text, source, ct);

    private async Task<BotMessage> LinkAsync(HandleTelegramMessageCommand command, string code, CancellationToken ct)
    {
        var hash = TelegramChat.HashOf(code);
        var chat = await db.TelegramChats.FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (chat is null || (await users.GetBlockedIdsAsync(ct)).Contains(chat.UserId))
            return new BotMessage("Код не найден или устарел — получите новый");

        if (!chat.CodeValid(clock.GetUtcNow()))
            return new BotMessage("Код привязки устарел — получите новый");

        // Чат — у одного пользователя: прежняя привязка этого чата к другому снимается.
        // Отдельным сохранением до привязки: порядок UPDATE в одном сохранении не гарантирован, а ChatId уникален
        var others = await db.TelegramChats.Where(c => c.ChatId == command.ChatId && c.Id != chat.Id).ToListAsync(ct);
        if (others.Count > 0)
        {
            others.ForEach(o => o.Unlink());
            await db.SaveChangesAsync(ct);
        }

        chat.Link(command.ChatId, command.Username, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        var user = await users.FindAsync(chat.UserId, ct);
        return new BotMessage($"Чат привязан к {user?.Email ?? "учётной записи"}.\n\n{BotTexts.Help}") { Menu = BotMenu.Show };
    }
}
