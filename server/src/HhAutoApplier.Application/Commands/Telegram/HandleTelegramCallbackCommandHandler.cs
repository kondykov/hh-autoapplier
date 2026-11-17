using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;

namespace HhAutoApplier.Application.Commands.Telegram;

// Кнопки под сообщениями бота. Разделы (#115) действуют через те же команды и запросы, что веб-интерфейс, от имени
// пользователя привязанного чата: подделанная callback_data не откроет чужого — сценарии ищут только среди его данных
public class HandleTelegramCallbackCommandHandler(
    BotChat bot,
    IAppDbContext db,
    IEnumerable<IBotSection> sections
) : ICommandHandler<HandleTelegramCallbackCommand, BotCallbackResult>
{
    public async Task<BotCallbackResult> Handle(HandleTelegramCallbackCommand command, CancellationToken ct = default)
    {
        var (state, chat) = await bot.EnterAsync(command.ChatId, ct);
        if (state != BotEntryState.Linked)
            return new BotCallbackResult("Чат не привязан", null);

        if (!command.Data.Contains(':'))
            return new BotCallbackResult("Неизвестная кнопка", null);

        var press = BotPress.Parse(command.Data, command.Source);
        switch (press.Action)
        {
            case BotTexts.Unlink:
                chat!.Unlink();
                await db.SaveChangesAsync(ct);
                return new BotCallbackResult("Чат отвязан", "Чат отвязан: уведомлений больше не будет. Привязать снова — новым кодом: /start <код>")
                {
                    Replies = [new BotMessage("Меню убрано.") { Menu = BotMenu.Remove }],
                };
            case BotTexts.Cancel:
                return new BotCallbackResult("Отменено", "Отменено");
        }

        var section = sections.FirstOrDefault(s => s.Actions.Contains(press.Action));
        return section is null ? new BotCallbackResult("Неизвестная кнопка", null) : await section.PressAsync(press, ct);
    }
}
