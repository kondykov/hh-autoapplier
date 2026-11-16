namespace Postbox;

/// <summary>Чтение и разбор ящика.</summary>
public interface IMailboxReader
{
    /// <summary>Папки ящика с числом писем.</summary>
    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>Последние письма по условиям, новые первыми. Письма не помечаются прочитанными.</summary>
    Task<IReadOnlyList<EmailSummary>> SearchAsync(MailSearch? search = null, CancellationToken cancellationToken = default);

    /// <summary>Письмо целиком: тексты и вложения. Прочитанным не помечается — для этого <see cref="SetReadAsync"/>.</summary>
    /// <exception cref="MailException">С <see cref="MailErrorKind.NotFound"/> — письма или папки больше нет.</exception>
    Task<ReceivedEmail> GetAsync(EmailId id, CancellationToken cancellationToken = default);

    /// <summary>Помечает письма прочитанными или непрочитанными.</summary>
    Task SetReadAsync(IReadOnlyCollection<EmailId> ids, bool isRead = true, CancellationToken cancellationToken = default);

    /// <summary>Переносит письма в папку. Старые <see cref="EmailId"/> после переноса недействительны.</summary>
    Task MoveAsync(IReadOnlyCollection<EmailId> ids, string targetFolder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет письма насовсем. Если сервер не умеет UIDPLUS, вычищаются все письма папки с пометкой «удалено».
    /// Чтобы оставить возможность восстановить — перенести в корзину через <see cref="MoveAsync"/>.
    /// </summary>
    Task DeleteAsync(IReadOnlyCollection<EmailId> ids, CancellationToken cancellationToken = default);
}

/// <summary>Короткие формы для одного письма.</summary>
public static class MailboxReaderExtensions
{
    /// <summary>Помечает письмо прочитанным или непрочитанным.</summary>
    public static Task SetReadAsync(this IMailboxReader reader, EmailId id, bool isRead = true, CancellationToken cancellationToken = default) =>
        reader.SetReadAsync([id], isRead, cancellationToken);

    /// <summary>Переносит письмо в папку.</summary>
    public static Task MoveAsync(this IMailboxReader reader, EmailId id, string targetFolder, CancellationToken cancellationToken = default) =>
        reader.MoveAsync([id], targetFolder, cancellationToken);

    /// <summary>Удаляет письмо насовсем.</summary>
    public static Task DeleteAsync(this IMailboxReader reader, EmailId id, CancellationToken cancellationToken = default) =>
        reader.DeleteAsync([id], cancellationToken);
}
