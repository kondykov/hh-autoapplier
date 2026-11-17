using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Domain.Telegram;
using Microsoft.EntityFrameworkCore;

namespace HhAutoApplier.Application.Telegram;

// Нажатие кнопки раздела: callback_data «действие:аргументы» (до 64 байт)
public record BotPress(string Action, IReadOnlyList<string> Args)
{
    public string Arg(int index) => index < Args.Count ? Args[index] : "";

    public string Data => Args.Count == 0 ? $"{Action}:" : $"{Action}:{string.Join(':', Args)}";

    public static BotPress Parse(string data)
    {
        var parts = data.Split(':');
        // «действие:» — без аргументов
        return new BotPress(parts[0], parts[1..] is [""] ? [] : parts[1..]);
    }
}

// Раздел бота (#115): экраны и кнопки одной части веб-интерфейса. Действия — те же команды и запросы Application,
// что у контроллеров: пользователь чата для них — ICurrentUser (ActingUser), права проверяются там же, где для веба
public interface IBotSection
{
    // Действия кнопок раздела — первая часть callback_data
    IReadOnlyCollection<string> Actions { get; }

    Task<BotResult> PressAsync(BotPress press, CancellationToken ct);
}

public static class BotResults
{
    public static BotResult Notice(string notice) => new() { Notice = notice };

    // Перерисовать экран
    public static BotResult Show(BotMessage screen, string notice = "") => new() { Notice = notice, Screen = screen };
}

// Что ждёт экран в режиме ввода (TelegramChat.InputKind)
public static class BotInput
{
    public const string Letter = "letter";

    public const string Answer = "answer";

    public const string DeclineReason = "decline";

    public const string SkipReason = "skip";

    // Кнопка «Отменить» режима ввода: экран возвращается туда, откуда пришёл (TelegramChat.InputBack)
    public const string Cancel = "ix";

    // Экран, который ждёт текст
    public static BotMessage Screen(string html) => new(html) { Html = true, Rows = [[new BotButton("✖️ Отменить", $"{Cancel}:")]] };
}

public enum BotEntryState
{
    Linked,
    NotLinked,
    Blocked,
}

// Вход в чат: привязан ли он и к кому. Пользователь чата становится пользователем сценариев (ActingUser) до конца обновления;
// чат (Current) — это экран, режим ввода и черновик, их правят разделы и сохраняют вместе со своим сценарием
public class BotChat(IAppDbContext db, IUserService users, ActingUser acting, TimeProvider clock)
{
    public TelegramChat? Current { get; private set; }

    public DateTimeOffset Now => clock.GetUtcNow();

    public async Task<BotEntryState> EnterAsync(long chatId, CancellationToken ct)
    {
        var chat = await db.TelegramChats.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        if (chat is null || await users.FindAsync(chat.UserId, ct) is not { } user)
            return BotEntryState.NotLinked;

        Current = chat;
        if (user.Blocked)
            return BotEntryState.Blocked;

        acting.Act(user);
        return BotEntryState.Linked;
    }

    private TelegramChat Chat => Current ?? throw new InvalidOperationException("Чат не открыт: сначала EnterAsync");

    // Перевести экран в режим ввода; back — callback_data экрана, куда вернуться по «Отменить»
    public async Task AwaitInputAsync(string kind, string target, int? number, string back, CancellationToken ct)
    {
        Chat.AwaitInput(kind, target, number, back, Now);
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearInputAsync(CancellationToken ct)
    {
        Chat.ClearInput();
        await db.SaveChangesAsync(ct);
    }

    // Черновик правки — только для этой вакансии и с тем же числом блоков; иначе его нет
    public IReadOnlyList<string>? Draft(string vacancyId, int blocks) =>
        Chat.DraftVacancyId == vacancyId && Chat.DraftBlocks.Count == blocks ? Chat.DraftBlocks : null;

    public async Task SaveDraftAsync(string vacancyId, IReadOnlyCollection<string> blocks, CancellationToken ct)
    {
        Chat.SaveDraft(vacancyId, blocks);
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearDraftAsync(CancellationToken ct)
    {
        Chat.ClearDraft();
        await db.SaveChangesAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
