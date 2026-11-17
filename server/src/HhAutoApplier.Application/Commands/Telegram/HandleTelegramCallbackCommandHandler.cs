using HhAutoApplier.Application.Cqrs;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;

namespace HhAutoApplier.Application.Commands.Telegram;

// Кнопки под сообщениями бота (#115, «один экран»). Нажатие на экране перерисовывает его на месте; «Открыть» под уведомлением
// присылает экран заново внизу чата; кнопки решения под уведомлением сворачивают его до итога.
// Разделы действуют через те же команды и запросы, что веб-интерфейс, от имени пользователя привязанного чата:
// подделанная callback_data не откроет чужого — сценарии ищут только среди его данных
public class HandleTelegramCallbackCommandHandler(BotChat bot, IEnumerable<IBotSection> sections)
    : ICommandHandler<HandleTelegramCallbackCommand, BotResult>
{
    // Кнопки уведомлений прежних версий, которые открывают экран: экраном станет новое сообщение, уведомление останется
    private static readonly string[] OpenFromNotification = [BotTexts.ShowReview, BotTexts.ReviewCard];

    public async Task<BotResult> Handle(HandleTelegramCallbackCommand command, CancellationToken ct = default)
    {
        if (await bot.EnterAsync(command.ChatId, ct) != BotEntryState.Linked)
            return BotResults.Notice("Чат не привязан");

        var chat = bot.Current!;
        if (!command.Data.Contains(':'))
            return BotResults.Notice("Неизвестная кнопка");

        var press = BotPress.Parse(command.Data);
        if (press.Action == BotTexts.Dismiss)
            return new BotResult { DeletePressed = true };

        if (press.Action == BotTexts.Open || (OpenFromNotification.Contains(press.Action) && command.MessageId != chat.ScreenMessageId))
        {
            // Сводку открыли — следующее событие начнёт новую
            if (command.MessageId != 0 && command.MessageId == chat.DigestMessageId)
                chat.ResetDigest();
            chat.ClearInput();
            await bot.SaveAsync(ct);
            var inner = press.Action == BotTexts.Open ? BotPress.Parse(string.Join(':', press.Args)) : press;
            var opened = await RouteAsync(inner, ct);
            return opened with { MoveScreen = opened.Screen is not null, ScreenMessageId = chat.ScreenMessageId };
        }

        // «Отменить» в режиме ввода — вернуться туда, откуда пришли
        if (press.Action == BotInput.Cancel)
            press = BotPress.Parse(chat.InputBack ?? $"{MenuSection.Show}:");

        // Любое нажатие снимает режим ввода; раздел включит его заново, если кнопка — «✏️ …»
        chat.ClearInput();
        var result = await RouteAsync(press, ct);
        if (result.Screen is null)
        {
            await bot.SaveAsync(ct);
            return result;
        }

        // Нажатое сообщение и есть экран
        if (command.MessageId > 0)
            chat.ShowScreen(command.MessageId);
        await bot.SaveAsync(ct);
        return result with { ScreenMessageId = chat.ScreenMessageId };
    }

    private async Task<BotResult> RouteAsync(BotPress press, CancellationToken ct)
    {
        var section = sections.FirstOrDefault(s => s.Actions.Contains(press.Action));
        return section is null ? BotResults.Notice("Неизвестная кнопка") : await section.PressAsync(press, ct);
    }
}
