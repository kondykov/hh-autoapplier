using System.Net;
using HhAutoApplier.Application.Commands.Telegram;
using HhAutoApplier.Application.Dtos;
using HhAutoApplier.Application.Notifications;
using HhAutoApplier.Application.Telegram;
using HhAutoApplier.Domain.Notifications;
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
            var replies = await handler.Handle(new HandleTelegramMessageCommand
            {
                ChatId = message.Chat.Id,
                IsPrivate = message.Chat.Type == ChatType.Private,
                Text = text,
                Username = message.From?.Username,
                ReplyTo = message.ReplyToMessage is { } replied ? Source(client, replied) : null,
            }, ct);
            await SendRepliesAsync(client, message.Chat.Id, replies, ct);
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
            var result = await handler.Handle(new HandleTelegramCallbackCommand { ChatId = source.Chat.Id, Data = data, Source = Source(client, source) }, ct);
            await client.AnswerCallbackQuery(callback.Id, result.Notice == "" ? null : result.Notice, cancellationToken: ct);
            if (result.Edit is { } screen)
                await EditAsync(client, source, screen, ct);
            else if (result.Status is not null)
            {
                var edited = $"{source.Text}\n\n{result.Status}";
                // Длинное сообщение (письмо, ответ работодателя) не дописать сверх лимита — только поменять кнопки.
                // Разметка исходного текста сохраняется: дописанное идёт в конец, смещения не меняются
                if (source.Text is not null && edited.Length <= MaxText)
                    await IgnoreUnchanged(client.EditMessageText(source.Chat.Id, source.MessageId, edited, entities: source.Entities,
                        replyMarkup: Keyboard(result.StatusRows), linkPreviewOptions: NoPreview, cancellationToken: ct));
                else
                    await IgnoreUnchanged(client.EditMessageReplyMarkup(source.Chat.Id, source.MessageId, Keyboard(result.StatusRows), cancellationToken: ct));
            }

            await SendRepliesAsync(client, source.Chat.Id, result.Replies, ct);
        }
    }

    // Сообщение бота, на которое ответили или под которым нажали: текст и блоки <pre> (черновик письма, #115).
    // Смещения сущностей Telegram — в UTF-16, как у строк .NET
    private static BotSource Source(ITelegramBotClient client, Message message)
    {
        var text = message.Text ?? message.Caption ?? "";
        var blocks = (message.Entities ?? []).Where(e => e.Type == MessageEntityType.Pre && e.Offset + e.Length <= text.Length)
            .Select(e => text.Substring(e.Offset, e.Length)).ToList();
        return new BotSource(text, blocks, message.From?.Id == client.BotId);
    }

    // Листание на месте: текст и кнопки сообщения заменяются новым экраном
    private static async Task EditAsync(ITelegramBotClient client, Message source, BotMessage screen, CancellationToken ct)
    {
        var fitted = BotHtml.Fit(screen);
        await IgnoreUnchanged(client.EditMessageText(source.Chat.Id, source.MessageId, fitted.Text, fitted.Html ? ParseMode.Html : ParseMode.None,
            replyMarkup: Keyboard(fitted.Rows), linkPreviewOptions: NoPreview, cancellationToken: ct));
    }

    // Повторное нажатие «Обновить» без изменений: Telegram отвечает ошибкой «message is not modified» — это не сбой
    private static async Task IgnoreUnchanged(Task edit)
    {
        try
        {
            await edit;
        }
        catch (ApiRequestException e) when (e.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static async Task SendRepliesAsync(ITelegramBotClient client, long chatId, IReadOnlyList<BotMessage> replies, CancellationToken ct)
    {
        foreach (var reply in replies.Select(BotHtml.Fit))
        {
            var mode = reply.Html ? ParseMode.Html : ParseMode.None;
            if (reply.File is { } file)
            {
                await using var stream = new MemoryStream(file.Content);
                await client.SendDocument(chatId, InputFile.FromStream(stream, file.Name), reply.Text, mode, replyMarkup: Markup(reply), cancellationToken: ct);
            }
            else
                await client.SendMessage(chatId, reply.Text, mode, replyMarkup: Markup(reply), linkPreviewOptions: NoPreview, cancellationToken: ct);
        }
    }

    // Кнопки под сообщением, просьба ответить или меню внизу чата: у сообщения Telegram одна разметка
    private static ReplyMarkup? Markup(BotMessage message) => message switch
    {
        { AskReply: { } placeholder } => new ForceReplyMarkup { InputFieldPlaceholder = placeholder.Length > 64 ? placeholder[..64] : placeholder },
        { Rows.Count: > 0 } => Keyboard(message.Rows),
        { Menu: BotMenu.Show } => new ReplyKeyboardMarkup(BotTexts.MenuRows.Select(row => row.Select(text => new KeyboardButton(text))))
        {
            ResizeKeyboard = true,
            IsPersistent = true,
        },
        { Menu: BotMenu.Remove } => new ReplyKeyboardRemove(),
        _ => null,
    };

    // Меню команд («/» в поле ввода); не вышло — бот работает и без него
    private async Task SetCommandsAsync(ITelegramBotClient client, CancellationToken ct)
    {
        try
        {
            await client.SetMyCommands(BotTexts.Commands.Select(c => new BotCommand(c.Command, c.Description)), cancellationToken: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Telegram: меню команд не установлено — {Message}", e.Message);
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

    private async Task SendPendingAsync(ITelegramBotClient client, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Notifications.Where(n => n.SentAt == null && n.Attempts < Notification.MaxAttempts)
            .OrderBy(n => n.Id).Take(20).ToListAsync(ct);
        if (pending.Count == 0)
            return;

        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var chats = await db.TelegramChats.AsNoTracking().Where(c => userIds.Contains(c.UserId) && c.ChatId != null)
            .ToDictionaryAsync(c => c.UserId, c => c.ChatId!.Value, ct);

        foreach (var notification in pending)
        {
            if (!chats.TryGetValue(notification.UserId, out var chatId))
            {
                notification.Cancel("чат отвязан");
                continue;
            }

            var buttons = BotTexts.NotificationButtons(notification.Kind, notification.Key, notification.VacancyId);
            try
            {
                await client.SendMessage(chatId, BotHtml.Fit(new BotMessage(notification.Text)).Text, replyMarkup: Keyboard(buttons), linkPreviewOptions: NoPreview, cancellationToken: ct);
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
    // Ряды кнопок: кнопка-ссылка открывает адрес, остальные возвращают Data боту
    private static InlineKeyboardMarkup? Keyboard(IReadOnlyList<IReadOnlyList<BotButton>> rows) =>
        rows.All(r => r.Count == 0) ? null
            : new InlineKeyboardMarkup(rows.Where(r => r.Count > 0).Select(r => r.Select(b =>
                b.Url is { } url ? InlineKeyboardButton.WithUrl(b.Text, url) : InlineKeyboardButton.WithCallbackData(b.Text, b.Data))));
}
