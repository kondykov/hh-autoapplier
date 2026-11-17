using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;
using HhAutoApplier.Domain.Telegram;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Commands.Telegram;

// Сообщения боту (#115, «один экран»): /start <код> привязывает чат; команды присылают экран заново внизу чата, а сама команда
// удаляется; текст, когда экран ждёт ввода, идёт в режим ввода (правка письма, причина) и тоже удаляется.
// Всё, кроме привязки, — только в привязанном чате незаблокированного пользователя
public class HandleTelegramMessageCommandHandler(
    IAppDbContext db,
    IUserService users,
    BotChat bot,
    MenuSection menu,
    OverviewSection overview,
    ReviewSection review,
    VacanciesSection vacancies,
    DialogsSection dialogs,
    SearchesSection searches,
    CollectionSection collection,
    TimeProvider clock
) : ICommandHandler<HandleTelegramMessageCommand, BotResult>
{
    // Снять клавиатуру-меню прежних версий: служебное сообщение, удаляется сразу
    private static readonly BotMessage RemoveKeyboard = new("Меню — кнопкой /menu") { RemoveKeyboard = true, DeleteAfterSend = true };

    public async Task<BotResult> Handle(HandleTelegramMessageCommand command, CancellationToken ct = default)
    {
        if (!command.IsPrivate)
            return new BotResult();

        var trimmed = command.Text.Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // В группах Telegram дописывает имя бота: /status@my_bot. Кнопки клавиатуры прежних версий присылают свою подпись
        var name = trimmed switch
        {
            BotTexts.OldStatusButton => "/overview",
            BotTexts.OldReviewButton => "/review",
            BotTexts.OldHelpButton => "/help",
            _ => parts.Length > 0 && parts[0].StartsWith('/') ? parts[0].Split('@')[0].ToLowerInvariant() : "",
        };
        var argument = parts.Length > 1 ? parts[1] : "";

        if (name == "/start" && argument != "")
            return await LinkAsync(command, argument, ct);

        switch (await bot.EnterAsync(command.ChatId, ct))
        {
            case BotEntryState.NotLinked:
                return new BotResult { Messages = [new BotMessage(BotTexts.NotLinked)] };
            case BotEntryState.Blocked:
                return new BotResult { Messages = [new BotMessage("Учётная запись заблокирована")] };
        }

        var chat = bot.Current!;
        var remove = command.MessageId > 0 ? new[] { command.MessageId } : [];

        // Текст для экрана в режиме ввода; команда в это время — всё равно команда (и снимает режим)
        if (name == "" && chat.AwaitsInput(bot.Now))
        {
            var (kind, target, number, back) = (chat.InputKind!, chat.InputTarget!, chat.InputNumber, chat.InputBack ?? $"{MenuSection.Show}:");
            chat.ClearInput();
            await bot.SaveAsync(ct);
            var input = kind == BotInput.SkipReason
                ? await vacancies.SkipAsync(target, trimmed, back, ct)
                : await review.InputAsync(kind, target, number, command.Text, ct);
            return input with { ScreenMessageId = chat.ScreenMessageId, Delete = remove };
        }

        var expired = chat.InputKind is not null;
        chat.ClearInput();
        await bot.SaveAsync(ct);

        BotMessage screen;
        var messages = new List<BotMessage>();
        switch (name)
        {
            case "/menu" or "/start":
                screen = await menu.ScreenAsync(ct);
                messages.Add(RemoveKeyboard);
                break;
            case "/overview" or "/status":
                screen = await overview.ScreenAsync(ct);
                break;
            case "/review":
                screen = await review.CardAsync(null, 0, ct);
                break;
            case "/dialogs":
                screen = await dialogs.ListAsync(0, false, ct);
                break;
            case "/vacancies":
                screen = await vacancies.MenuAsync(ct);
                break;
            case "/searches":
                screen = await searches.ScreenAsync(ct);
                break;
            case "/collect":
                screen = await collection.ScreenAsync(ct);
                break;
            case "/report":
                return new BotResult { Messages = [await overview.ReportAsync(ct)], Delete = remove };
            case "/stop":
                // Отвязка — кнопкой подтверждения: привязать обратно можно только новым кодом
                screen = MenuSection.StopScreen();
                break;
            case "/help":
                screen = MenuSection.HelpScreen();
                break;
            default:
                // Просто текст: экран не ждал ввода (или время ввода вышло) — экран заново внизу с подсказкой
                var menuScreen = await menu.ScreenAsync(ct);
                var hint = expired
                    ? "⌛ Время ввода вышло (30 минут) — нажмите «✏️ …» снова.\n\n"
                    : "Текст принимается, когда экран его ждёт: «✏️ Письмо», «❌ С причиной». Команды — /menu, /help.\n\n";
                screen = menuScreen with { Text = $"{BotHtml.E(hint)}{menuScreen.Text}", Html = true };
                break;
        }

        return new BotResult { Screen = screen, MoveScreen = true, ScreenMessageId = chat.ScreenMessageId, Delete = remove, Messages = messages };
    }

    private async Task<BotResult> LinkAsync(HandleTelegramMessageCommand command, string code, CancellationToken ct)
    {
        var hash = TelegramChat.HashOf(code);
        var chat = await db.TelegramChats.FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (chat is null || (await users.GetBlockedIdsAsync(ct)).Contains(chat.UserId))
            return new BotResult { Messages = [new BotMessage("Код не найден или устарел — получите новый")] };

        if (!chat.CodeValid(clock.GetUtcNow()))
            return new BotResult { Messages = [new BotMessage("Код привязки устарел — получите новый")] };

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

        // Сразу и экран: дальше всё — на нём
        await bot.EnterAsync(command.ChatId, ct);
        return new BotResult
        {
            Messages = [new BotMessage($"Чат привязан к {user?.Email ?? "учётной записи"}.") { RemoveKeyboard = true }],
            Screen = await menu.ScreenAsync(ct),
            MoveScreen = true,
        };
    }
}
