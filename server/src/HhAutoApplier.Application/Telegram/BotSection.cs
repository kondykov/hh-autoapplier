using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Telegram;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Telegram;

// Нажатие кнопки раздела: callback_data «действие:аргументы» (до 64 байт); Source — сообщение с кнопкой
public record BotPress(string Action, IReadOnlyList<string> Args, BotSource? Source)
{
    public string Arg(int index) => index < Args.Count ? Args[index] : "";

    public static BotPress Parse(string data, BotSource? source)
    {
        var parts = data.Split(':');
        return new BotPress(parts[0], parts[1..], source);
    }
}

// Раздел бота (#115): экраны и кнопки одной части веб-интерфейса. Действия — те же команды и запросы Application,
// что у контроллеров: пользователь чата для них — ICurrentUser (ActingUser), права проверяются там же, где для веба
public interface IBotSection
{
    // Действия кнопок раздела — первая часть callback_data
    IReadOnlyCollection<string> Actions { get; }

    Task<BotCallbackResult> PressAsync(BotPress press, CancellationToken ct);
}

public static class BotResults
{
    public static BotCallbackResult Notice(string notice) => new(notice, null);

    // Листание на месте: сообщение с кнопкой заменяется новым экраном
    public static BotCallbackResult Edit(BotMessage screen, string notice = "") => new(notice, null) { Edit = screen };

    // Новое сообщение в чат, нажатое не меняется
    public static BotCallbackResult Reply(BotMessage message, string notice = "") => new(notice, null) { Replies = [message] };
}

public enum BotEntryState
{
    Linked,
    NotLinked,
    Blocked,
}

// Вход в чат: привязан ли он и к кому. Пользователь чата становится пользователем сценариев (ActingUser) до конца обновления
public class BotChat(IAppDbContext db, IUserService users, ActingUser acting)
{
    public async Task<(BotEntryState State, TelegramChat? Chat)> EnterAsync(long chatId, CancellationToken ct)
    {
        var chat = await db.TelegramChats.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        if (chat is null)
            return (BotEntryState.NotLinked, null);

        var user = await users.FindAsync(chat.UserId, ct);
        if (user is null)
            return (BotEntryState.NotLinked, null);

        if (user.Blocked)
            return (BotEntryState.Blocked, chat);

        acting.Act(user);
        return (BotEntryState.Linked, chat);
    }
}
