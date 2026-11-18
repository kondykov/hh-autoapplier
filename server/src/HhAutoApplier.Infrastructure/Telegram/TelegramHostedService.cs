using System.Net;
using HhAutoApplier.Application;
using HhAutoApplier.Application.Abstractions;
using HhAutoApplier.Application.Commands.Telegram;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;
using HhAutoApplier.Domain.Notifications;
using HhAutoApplier.Domain.Telegram;
using HhAutoApplier.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace HhAutoApplier.Infrastructure.Telegram;

// Бот: приём сообщений и нажатий через long polling (сервер без адреса в интернете — вебхук не нужен)
// и доставка outbox уведомлений. Логика команд — в обработчиках Application, здесь только транспорт
public class TelegramHostedService(
    TelegramBot bot,
    IServiceScopeFactory scopes,
    IOptions<TelegramOptions> options,
    IOptions<StageOptions> stage,
    TimeProvider clock,
    ILogger<TelegramHostedService> logger
) : BackgroundService
{
    private static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(10);

    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    // Лимит текста сообщения Telegram
    private const int MaxText = 4096;

    protected override Task ExecuteAsync(CancellationToken stop)
    {
        if (bot.Client is not { } client)
        {
            logger.LogInformation("Telegram: бот выключен (нет TELEGRAM_BOT_TOKEN)");
            return Task.CompletedTask;
        }

        return Task.WhenAll(SetCommandsAsync(client, stop), ReceiveAsync(client, stop), SendAsync(client, stop));
    }

    private async Task ReceiveAsync(ITelegramBotClient client, CancellationToken stop)
    {
        var timeout = Math.Clamp(options.Value.PollTimeoutSeconds, 1, 50);
        int? offset = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var updates = await client.GetUpdates(offset, 100, timeout, [UpdateType.Message, UpdateType.CallbackQuery], stop);
                foreach (var update in updates)
                {
                    // Сдвиг до обработки: сообщение, уронившее обработчик, не будет приходить снова и снова
                    offset = update.Id + 1;
                    try
                    {
                        await HandleAsync(client, update, stop);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogError(e, "Telegram: обновление {Id} не обработано", update.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (ApiRequestException e)
            {
                // 409 — у бота webhook или тот же токен опрашивает другой экземпляр; 401 — неверный токен. Без стека: повторяется до исправления
                logger.LogWarning("Telegram: не удалось получить обновления — {Code} {Message}", e.ErrorCode, e.Message);
                await Task.Delay(ErrorPause, stop);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Telegram: не удалось получить обновления");
                await Task.Delay(ErrorPause, stop);
            }
        }
    }

    private async Task HandleAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (update.Message is { Text: { } text } message)
        {
            var handler = scope.ServiceProvider.GetRequiredService<HandleTelegramMessageCommandHandler>();
            var result = await handler.Handle(new HandleTelegramMessageCommand
            {
                ChatId = message.Chat.Id,
                IsPrivate = message.Chat.Type == ChatType.Private,
                Text = text,
                Username = message.From?.Username,
                MessageId = message.MessageId,
            }, ct);
            await ApplyAsync(client, scope.ServiceProvider, message.Chat.Id, result, null, ct);
        }
        else if (update.CallbackQuery is { Data: { } data, Message: { } source } callback)
        {
            // Кнопки — только в личном чате с ботом: в группе нажать мог бы любой участник
            if (source.Chat.Type != ChatType.Private)
            {
                await client.AnswerCallbackQuery(callback.Id, "Только в личном чате", cancellationToken: ct);
                return;
            }

            var handler = scope.ServiceProvider.GetRequiredService<HandleTelegramCallbackCommandHandler>();
            var result = await handler.Handle(new HandleTelegramCallbackCommand { ChatId = source.Chat.Id, Data = data, MessageId = source.MessageId }, ct);
            await client.AnswerCallbackQuery(callback.Id, result.Notice == "" ? null : result.Notice, cancellationToken: ct);
            await ApplyAsync(client, scope.ServiceProvider, source.Chat.Id, result, source, ct);
        }
    }

    // Ответ бота (#115, «один экран»): служебные сообщения, свернуть или убрать нажатое уведомление, перерисовать экран, убрать ввод.
    // Экран, который не правится (удалён, чужой, старше лимита Telegram), молча присылается заново и запоминается
    private async Task ApplyAsync(ITelegramBotClient client, IServiceProvider services, long chatId, BotResult result, Message? pressed, CancellationToken ct)
    {
        foreach (var message in result.Messages)
        {
            var sent = await SendAsync(client, chatId, message, ct);
            if (message.DeleteAfterSend)
                await TryDeleteAsync(client, chatId, sent.MessageId, ct);
        }

        if (pressed is not null && result.Status is not null)
        {
            // Уведомление сворачивается до первой строки и итога
            var first = (pressed.Text ?? pressed.Caption ?? "").Split('\n', 2)[0];
            await TryAsync(client.EditMessageText(chatId, pressed.MessageId, BotHtml.Cut($"{first}\n{result.Status}", MaxText),
                replyMarkup: Keyboard(result.StatusRows), linkPreviewOptions: NoPreview, cancellationToken: ct));
        }

        if (pressed is not null && result.DeletePressed && !await TryDeleteAsync(client, chatId, pressed.MessageId, ct))
            await TryAsync(client.EditMessageReplyMarkup(chatId, pressed.MessageId, null, cancellationToken: ct));

        if (result.Screen is { } screen)
        {
            var fitted = BotHtml.Fit(Staged(screen));
            var edited = false;
            if (result.MoveScreen && result.ScreenMessageId is { } old)
                await TryDeleteAsync(client, chatId, old, ct);
            else if (result.ScreenMessageId is { } id)
                edited = await TryAsync(client.EditMessageText(chatId, id, fitted.Text, fitted.Html ? ParseMode.Html : ParseMode.None,
                    replyMarkup: Keyboard(fitted.Rows), linkPreviewOptions: NoPreview, cancellationToken: ct));

            if (!edited)
            {
                var sent = await SendAsync(client, chatId, fitted, ct);
                await services.GetRequiredService<RememberTelegramScreenCommandHandler>().Handle(new RememberTelegramScreenCommand(chatId, sent.MessageId), ct);
            }
        }

        foreach (var id in result.Delete)
            await TryDeleteAsync(client, chatId, id, ct);

        if (result.CommandsMenu is { } menu)
            await SetChatCommandsAsync(client, chatId, menu, ct);
    }

    // Правка или удаление, которым Telegram может отказать: сообщение удалено, старше 48 часов, не изменилось.
    // Ответ «message is not modified» — успех: на экране уже то, что нужно
    private async Task<bool> TryAsync(Task action)
    {
        try
        {
            await action;
            return true;
        }
        catch (ApiRequestException e) when (e.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        catch (ApiRequestException e)
        {
            logger.LogDebug("Telegram: правка или удаление не прошли — {Message}", e.Message);
            return false;
        }
    }

    private Task<bool> TryDeleteAsync(ITelegramBotClient client, long chatId, int messageId, CancellationToken ct) =>
        TryAsync(client.DeleteMessage(chatId, messageId, ct));

    // Тестовый стенд (#124): экран и отдельные уведомления начинаются с метки, чтобы не спутать с боевым ботом
    private BotMessage Staged(BotMessage message)
    {
        if (!stage.Value.Enabled)
            return message;
        var line = stage.Value.BotLine;
        return message with { Text = $"{(message.Html ? BotHtml.E(line) : line)}\n{message.Text}" };
    }

    private static async Task<Message> SendAsync(ITelegramBotClient client, long chatId, BotMessage message, CancellationToken ct)
    {
        var reply = BotHtml.Fit(message);
        var mode = reply.Html ? ParseMode.Html : ParseMode.None;
        ReplyMarkup? markup = reply.RemoveKeyboard ? new ReplyKeyboardRemove() : Keyboard(reply.Rows);
        if (reply.File is { } file)
        {
            await using var stream = new MemoryStream(file.Content);
            return await client.SendDocument(chatId, InputFile.FromStream(stream, file.Name), reply.Text, mode, replyMarkup: markup, cancellationToken: ct);
        }

        return await client.SendMessage(chatId, reply.Text, mode, replyMarkup: markup, linkPreviewOptions: NoPreview, cancellationToken: ct);
    }

    // Меню команд («/» в поле ввода, #118): по умолчанию — только /start, полный список — каждому привязанному чату.
    // Не вышло — бот работает и без него
    private async Task SetCommandsAsync(ITelegramBotClient client, CancellationToken ct)
    {
        try
        {
            await client.SetMyCommands(Commands(BotTexts.StrangerCommands), BotCommandScope.Default(), cancellationToken: ct);
            await using var scope = scopes.CreateAsyncScope();
            var linked = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().TelegramChats
                .Where(c => c.ChatId != null).Select(c => c.ChatId!.Value).ToListAsync(ct);
            foreach (var chatId in linked)
                await SetChatCommandsAsync(client, chatId, BotCommandsMenu.Full, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Telegram: меню команд не установлено — {Message}", e.Message);
        }
    }

    private static IEnumerable<BotCommand> Commands(IEnumerable<(string Command, string Description)> list) =>
        list.Select(c => new BotCommand(c.Command, c.Description));

    // Меню одного чата: Full — свой полный список, Stranger — снять свой, останется меню по умолчанию
    private async Task SetChatCommandsAsync(ITelegramBotClient client, long chatId, BotCommandsMenu menu, CancellationToken ct)
    {
        try
        {
            if (menu == BotCommandsMenu.Full)
                await client.SetMyCommands(Commands(BotTexts.Commands), BotCommandScope.Chat(chatId), cancellationToken: ct);
            else
                await client.DeleteMyCommands(BotCommandScope.Chat(chatId), cancellationToken: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Telegram: меню команд чата не обновлено — {Message}", e.Message);
        }
    }

    // Outbox: неотправленные уведомления — в привязанные чаты, по порядку записи
    private async Task SendAsync(ITelegramBotClient client, CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.SendIntervalSeconds, 1, 300));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await SendPendingAsync(client, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Telegram: ошибка отправки уведомлений");
            }

            await Task.Delay(interval, stop);
        }
    }

    // Outbox: неотправленные уведомления — в привязанные чаты, по порядку записи. Важные — отдельным сообщением с кнопками,
    // обычные — строкой в сводку: пока она свежая и не открыта, дописывается правкой, без нового звука (#115)
    private async Task SendPendingAsync(ITelegramBotClient client, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Notifications.Where(n => n.SentAt == null && n.Attempts < Notification.MaxAttempts)
            .OrderBy(n => n.Id).Take(20).ToListAsync(ct);
        if (pending.Count == 0)
            return;

        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var chats = await db.TelegramChats.Where(c => userIds.Contains(c.UserId) && c.ChatId != null).ToDictionaryAsync(c => c.UserId, ct);
        var digest = scope.ServiceProvider.GetRequiredService<BotDigest>();

        foreach (var notification in pending)
        {
            if (!chats.TryGetValue(notification.UserId, out var chat))
            {
                notification.Cancel("чат отвязан");
                continue;
            }

            var chatId = chat.ChatId!.Value;
            try
            {
                if (BotTexts.Delivery(notification.Kind) == NotificationDelivery.Digest)
                    await DigestAsync(client, chat, chatId, BotTexts.Short(notification.Text, 400), await digest.ButtonsAsync(notification.UserId, ct), ct);
                else
                {
                    var buttons = BotTexts.NotificationButtons(notification.Kind, notification.Key, notification.VacancyId);
                    await client.SendMessage(chatId, BotHtml.Fit(Staged(new BotMessage(notification.Text))).Text, replyMarkup: Keyboard(buttons),
                        linkPreviewOptions: NoPreview, cancellationToken: ct);
                }

                notification.MarkSent(clock.GetUtcNow());
            }
            catch (ApiRequestException e) when (e.ErrorCode == (int)HttpStatusCode.Forbidden)
            {
                // Пользователь остановил бота — пока он не напишет боту снова, сообщения не дойдут
                notification.Cancel($"Telegram: {e.Message}");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                notification.MarkFailed(e.Message);
                logger.LogWarning(e, "Telegram: уведомление {Id} не отправлено", notification.Id);
            }

            await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
    }

    // Строка в сводку: дописать в прежнюю правкой или начать новую
    private async Task DigestAsync(ITelegramBotClient client, TelegramChat chat, long chatId, string line,
        IReadOnlyList<IReadOnlyList<BotButton>> buttons, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (chat.AppendToDigest(line, now) is { } text && chat.DigestMessageId is { } id
            && await TryAsync(client.EditMessageText(chatId, id, text, replyMarkup: Keyboard(buttons), linkPreviewOptions: NoPreview, cancellationToken: ct)))
        {
            chat.ShowDigest(id, text, now, started: false);
            return;
        }

        var fresh = $"🔔 Новое\n{line}";
        var sent = await client.SendMessage(chatId, fresh, replyMarkup: Keyboard(buttons), linkPreviewOptions: NoPreview, cancellationToken: ct);
        chat.ShowDigest(sent.MessageId, fresh, now, started: true);
    }

    // Ряды кнопок: кнопка-ссылка открывает адрес, остальные возвращают Data боту
    private static InlineKeyboardMarkup? Keyboard(IReadOnlyList<IReadOnlyList<BotButton>> rows) =>
        rows.All(r => r.Count == 0) ? null
            : new InlineKeyboardMarkup(rows.Where(r => r.Count > 0).Select(r => r.Select(b =>
                b.Url is { } url ? InlineKeyboardButton.WithUrl(b.Text, url) : InlineKeyboardButton.WithCallbackData(b.Text, b.Data))));
}
