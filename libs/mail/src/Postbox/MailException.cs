namespace Postbox;

/// <summary>Почему не удалась операция с почтой.</summary>
public enum MailErrorKind
{
    /// <summary>Письмо составлено неверно: нет адресатов, пустой адрес и т. п. Повтор не поможет.</summary>
    InvalidMessage,
    /// <summary>Сервер не принял логин или пароль.</summary>
    Authentication,
    /// <summary>Не удалось установить защищённое соединение (сертификат, версия TLS).</summary>
    Tls,
    /// <summary>Сервер недоступен или оборвал соединение.</summary>
    Connection,
    /// <summary>Сервер не ответил за отведённое время.</summary>
    Timeout,
    /// <summary>Сервер отказал: отправитель, адресат или письмо не приняты, команда отклонена.</summary>
    Rejected,
    /// <summary>Папка или письмо не найдены.</summary>
    NotFound,
    /// <summary>Сервер ответил не по протоколу.</summary>
    Protocol,
}

/// <summary>Ошибка отправки или чтения почты с понятной причиной.</summary>
public class MailException : Exception
{
    /// <summary>Создаёт ошибку с причиной.</summary>
    public MailException(MailErrorKind kind, string message, bool isTransient = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        IsTransient = isTransient;
    }

    /// <summary>Причина.</summary>
    public MailErrorKind Kind { get; }

    /// <summary>
    /// Временная ли ошибка: <c>true</c> — позже может пройти. Повторы по настройкам библиотека к этому моменту уже сделала.
    /// </summary>
    public bool IsTransient { get; }

    /// <summary>Сколько попыток сделано до этой ошибки.</summary>
    public int Attempts { get; internal set; } = 1;
}
