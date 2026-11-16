using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Postbox.Internal;

namespace Postbox;

/// <summary>
/// Чтение ящика по IMAP через MailKit. Каждый вызов — своё соединение: подходит для периодического опроса,
/// а не для постоянного слежения за ящиком.
/// </summary>
public sealed class ImapMailboxReader : IMailboxReader
{
    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags
        | MessageSummaryItems.InternalDate | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure;

    private readonly IOptionsMonitor<ImapOptions>? _monitor;
    private readonly ImapOptions? _fixed;
    private readonly ILogger _logger;

    /// <summary>Конструктор для DI.</summary>
    public ImapMailboxReader(IOptionsMonitor<ImapOptions> options, ILogger<ImapMailboxReader> logger)
    {
        _monitor = options;
        _logger = logger;
    }

    /// <summary>Конструктор без DI.</summary>
    /// <exception cref="OptionsValidationException">Настройки неверны.</exception>
    public ImapMailboxReader(ImapOptions options, ILogger? logger = null)
    {
        var result = new MailServerOptionsValidator<ImapOptions>().Validate(null, options);
        if (result.Failed)
            throw new OptionsValidationException(nameof(ImapOptions), typeof(ImapOptions), result.Failures);
        _fixed = options;
        _logger = logger ?? NullLogger.Instance;
    }

    private ImapOptions Options => _fixed ?? _monitor!.CurrentValue;

    /// <inheritdoc />
    public Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken = default) =>
        RunAsync<IReadOnlyList<MailFolderInfo>>("Список папок", async (client, token) =>
        {
            var folders = new List<IMailFolder>();
            foreach (var space in client.PersonalNamespaces)
                folders.AddRange(await client.GetFoldersAsync(space, false, token).ConfigureAwait(false));
            if (!folders.Any(f => f.FullName.Equals(client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)))
                folders.Insert(0, client.Inbox);

            var result = new List<MailFolderInfo>();
            foreach (var folder in folders)
            {
                if (folder.Attributes.HasFlag(FolderAttributes.NonExistent) || folder.Attributes.HasFlag(FolderAttributes.NoSelect))
                    continue;
                await folder.StatusAsync(StatusItems.Count | StatusItems.Unread, token).ConfigureAwait(false);
                result.Add(new MailFolderInfo(folder.FullName, folder.Name, RoleOf(folder, client), folder.Count, folder.Unread));
            }
            return result;
        }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<EmailSummary>> SearchAsync(MailSearch? search = null, CancellationToken cancellationToken = default)
    {
        search ??= new MailSearch();
        if (search.Limit < 1)
            throw new ArgumentOutOfRangeException(nameof(search), search.Limit, "MailSearch.Limit должен быть не меньше 1");

        return RunAsync<IReadOnlyList<EmailSummary>>("Поиск писем", async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, search.Folder ?? Options.DefaultFolder, FolderAccess.ReadOnly, token)
                .ConfigureAwait(false);
            var uids = await folder.SearchAsync(BuildQuery(search), token).ConfigureAwait(false);
            if (uids.Count == 0)
                return [];

            // UID растут с поступлением: последние Limit — самые новые. Since/Before IMAP сравнивает по дню, уточняем здесь
            var newest = uids.OrderByDescending(u => u.Id).Take(search.Limit).ToList();
            var summaries = await folder.FetchAsync(newest, SummaryItems, token).ConfigureAwait(false);
            return summaries
                .Where(s => (search.Since is null || ReceivedAt(s) >= search.Since) && (search.Before is null || ReceivedAt(s) < search.Before))
                .Select(s => ToSummary(folder, s))
                .OrderByDescending(s => s.Date)
                .ToList();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ReceivedEmail> GetAsync(EmailId id, CancellationToken cancellationToken = default) =>
        RunAsync("Чтение письма", async (client, token) =>
        {
            var folder = await OpenFolderAsync(client, id.Folder, FolderAccess.ReadOnly, token).ConfigureAwait(false);
            EnsureSameFolder(folder, id);
            var uid = new UniqueId(id.UidValidity, id.Uid);

            var flags = await folder.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate, token)
                .ConfigureAwait(false);
            if (flags.Count == 0)
                throw NotFound(id);
            // MailKit читает письмо через BODY.PEEK — флаг «прочитано» не ставится
            var message = await folder.GetMessageAsync(uid, token).ConfigureAwait(false);
            return ToReceived(id, message, flags[0]);
        }, cancellationToken);

    /// <inheritdoc />
    public Task SetReadAsync(IReadOnlyCollection<EmailId> ids, bool isRead = true, CancellationToken cancellationToken = default) =>
        ForEachFolderAsync(isRead ? "Пометка прочитанными" : "Пометка непрочитанными", ids, async (_, folder, uids, token) =>
        {
            var request = new StoreFlagsRequest(isRead ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true };
            await folder.StoreAsync(uids, request, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task MoveAsync(IReadOnlyCollection<EmailId> ids, string targetFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        return ForEachFolderAsync("Перенос писем", ids, async (client, folder, uids, token) =>
        {
            var target = await GetFolderAsync(client, targetFolder, token).ConfigureAwait(false);
            await folder.MoveToAsync(uids, target, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(IReadOnlyCollection<EmailId> ids, CancellationToken cancellationToken = default) =>
        ForEachFolderAsync("Удаление писем", ids, async (client, folder, uids, token) =>
        {
            await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, token)
                .ConfigureAwait(false);
            if (client.Capabilities.Contains(ImapCapability.UidPlus))
                await folder.ExpungeAsync(uids, token).ConfigureAwait(false);
            else
                await folder.ExpungeAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private Task ForEachFolderAsync(
        string operation,
        IReadOnlyCollection<EmailId> ids,
        Func<ImapClient, IMailFolder, IList<UniqueId>, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return Task.CompletedTask;

        return RunAsync(operation, async (client, token) =>
        {
            foreach (var group in ids.GroupBy(i => i.Folder))
            {
                var folder = await OpenFolderAsync(client, group.Key, FolderAccess.ReadWrite, token).ConfigureAwait(false);
                foreach (var id in group)
                    EnsureSameFolder(folder, id);
                var uids = group.Select(i => new UniqueId(i.UidValidity, i.Uid)).Distinct().ToList();
                await action(client, folder, uids, token).ConfigureAwait(false);
                await folder.CloseAsync(false, token).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);
    }

    private Task<T> RunAsync<T>(string operation, Func<ImapClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var options = Options;
        return MailRetry.RunAsync(async (_, token) =>
        {
            using var client = new ImapClient();
            try
            {
                await MailConnection.OpenAsync(client, options, token).ConfigureAwait(false);
                return await action(client, token).ConfigureAwait(false);
            }
            finally
            {
                await MailConnection.CloseAsync(client).ConfigureAwait(false);
            }
        }, options, _logger, operation, cancellationToken);
    }

    private static async Task<IMailFolder> GetFolderAsync(ImapClient client, string name, CancellationToken token) =>
        name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(name, token).ConfigureAwait(false);

    private static async Task<IMailFolder> OpenFolderAsync(ImapClient client, string name, FolderAccess access, CancellationToken token)
    {
        var folder = await GetFolderAsync(client, name, token).ConfigureAwait(false);
        await folder.OpenAsync(access, token).ConfigureAwait(false);
        return folder;
    }

    // UIDVALIDITY сменился — папку пересоздали, старые UID указывают не на те письма
    private static void EnsureSameFolder(IMailFolder folder, EmailId id)
    {
        if (folder.UidValidity != id.UidValidity)
            throw new MailException(MailErrorKind.NotFound,
                $"Папка '{id.Folder}' пересоздана на сервере (UIDVALIDITY {id.UidValidity} → {folder.UidValidity}): письмо {id} больше не адресуется");
    }

    private static MailException NotFound(EmailId id) =>
        new(MailErrorKind.NotFound, $"Письмо {id} не найдено: удалено или перенесено");

    private static SearchQuery BuildQuery(MailSearch search)
    {
        SearchQuery query = SearchQuery.All;
        if (search.UnreadOnly)
            query = query.And(SearchQuery.NotSeen);
        if (search.Since is { } since)
            query = query.And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)));
        if (search.Before is { } before)
            query = query.And(SearchQuery.DeliveredBefore(before.UtcDateTime.Date.AddDays(1)));
        if (!string.IsNullOrWhiteSpace(search.From))
            query = query.And(SearchQuery.FromContains(search.From));
        if (!string.IsNullOrWhiteSpace(search.Subject))
            query = query.And(SearchQuery.SubjectContains(search.Subject));
        if (!string.IsNullOrWhiteSpace(search.Text))
            query = query.And(SearchQuery.MessageContains(search.Text));
        return query;
    }

    private static DateTimeOffset ReceivedAt(IMessageSummary summary) =>
        summary.InternalDate ?? summary.Envelope?.Date ?? DateTimeOffset.MinValue;

    private static EmailSummary ToSummary(IMailFolder folder, IMessageSummary s) => new(
        new EmailId(folder.FullName, folder.UidValidity, s.UniqueId.Id),
        s.Envelope?.Subject ?? string.Empty,
        Addresses(s.Envelope?.From),
        Addresses(s.Envelope?.To),
        s.Envelope?.Date ?? ReceivedAt(s),
        s.Flags?.HasFlag(MessageFlags.Seen) == true,
        s.Attachments?.Any() == true,
        (long?)s.Size);

    internal static ReceivedEmail ToReceived(EmailId id, MimeMessage message, IMessageSummary? summary)
    {
        var attachments = new List<ReceivedAttachment>();
        foreach (var entity in message.BodyParts)
        {
            if (entity is MessagePart attached && (entity.IsAttachment || entity.ContentDisposition is null))
            {
                using var stream = new MemoryStream();
                attached.Message?.WriteTo(stream);
                var name = entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? "message.eml";
                attachments.Add(new ReceivedAttachment(name, entity.ContentType.MimeType, stream.ToArray(), entity.ContentId, false));
            }
            else if (entity is MimePart part && IsFile(part, message))
            {
                using var stream = new MemoryStream();
                part.Content?.DecodeTo(stream);
                var inline = !part.IsAttachment;
                var name = part.FileName ?? $"{(inline ? "inline" : "attachment")}-{attachments.Count + 1}{Extension(part.ContentType)}";
                attachments.Add(new ReceivedAttachment(name, part.ContentType.MimeType, stream.ToArray(), part.ContentId, inline));
            }
        }

        return new ReceivedEmail(
            id,
            message.MessageId,
            message.Subject ?? string.Empty,
            Addresses(message.From),
            Addresses(message.To),
            Addresses(message.Cc),
            Addresses(message.ReplyTo),
            message.Date == DateTimeOffset.MinValue && summary?.InternalDate is { } received ? received : message.Date,
            summary?.Flags?.HasFlag(MessageFlags.Seen) == true,
            message.TextBody,
            message.HtmlBody,
            attachments,
            message.Headers.Select(h => new KeyValuePair<string, string>(h.Field, h.Value)).ToList());
    }

    // Файл — всё, что приложено явно, и не-текстовые встроенные части (картинки в HTML). Сами тексты письма — не файлы
    private static bool IsFile(MimePart part, MimeMessage message)
    {
        if (part.IsAttachment)
            return true;
        if (part is TextPart)
            return false;
        return part.ContentId is not null || part.FileName is not null || !ReferenceEquals(part, message.Body);
    }

    private static string Extension(ContentType type) =>
        MimeTypes.TryGetExtension(type.MimeType, out var extension) ? extension : ".bin";

    private static IReadOnlyList<EmailAddress> Addresses(InternetAddressList? list) =>
        list?.Mailboxes.Select(EmailAddress.From).ToList() ?? [];

    private static MailFolderRole RoleOf(IMailFolder folder, ImapClient client)
    {
        var attributes = folder.Attributes;
        if (folder.FullName.Equals(client.Inbox.FullName, StringComparison.OrdinalIgnoreCase)) return MailFolderRole.Inbox;
        if (attributes.HasFlag(FolderAttributes.Sent)) return MailFolderRole.Sent;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return MailFolderRole.Drafts;
        if (attributes.HasFlag(FolderAttributes.Trash)) return MailFolderRole.Trash;
        if (attributes.HasFlag(FolderAttributes.Junk)) return MailFolderRole.Junk;
        if (attributes.HasFlag(FolderAttributes.Archive)) return MailFolderRole.Archive;
        return MailFolderRole.Normal;
    }
}
