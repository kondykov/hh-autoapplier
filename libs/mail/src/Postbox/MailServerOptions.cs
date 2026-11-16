namespace Postbox;

/// <summary>Как защищать соединение с сервером.</summary>
public enum MailSecurity
{
    /// <summary>По порту: 465 и 993 — TLS сразу, иначе STARTTLS, если сервер его предлагает.</summary>
    Auto,
    /// <summary>Без шифрования. Только для локальных и тестовых серверов.</summary>
    None,
    /// <summary>Обычное соединение, затем обязательный STARTTLS (SMTP 587, IMAP 143).</summary>
    StartTls,
    /// <summary>TLS с первого байта (SMTP 465, IMAP 993).</summary>
    SslOnConnect,
}

/// <summary>Общие настройки подключения к почтовому серверу.</summary>
public abstract class MailServerOptions
{
    /// <summary>Имя или адрес сервера, например <c>smtp.yandex.ru</c>.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Порт.</summary>
    public int Port { get; set; }

    /// <summary>Защита соединения.</summary>
    public MailSecurity Security { get; set; } = MailSecurity.Auto;

    /// <summary>Логин; пустой — без входа (SMTP-релей, тестовый сервер).</summary>
    public string? UserName { get; set; }

    /// <summary>Пароль (у Яндекса и Mail.ru — пароль приложения). Хранить в секретах, не в appsettings.</summary>
    public string? Password { get; set; }

    /// <summary>Сколько ждать ответа сервера на одну команду, секунд.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Сколько всего попыток при временных ошибках (сеть, таймаут, ответ 4xx). 1 — без повторов.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Пауза перед первым повтором, мс; дальше удваивается.</summary>
    public int RetryDelayMilliseconds { get; set; } = 1000;
}

/// <summary>Настройки отправки по SMTP.</summary>
public sealed class SmtpOptions : MailServerOptions
{
    /// <summary>Создаёт настройки с портом 587.</summary>
    public SmtpOptions() => Port = 587;

    /// <summary>Отправитель по умолчанию: <c>user@example.com</c> или <c>Имя &lt;user@example.com&gt;</c>.</summary>
    public string? DefaultFrom { get; set; }
}

/// <summary>Настройки чтения по IMAP.</summary>
public sealed class ImapOptions : MailServerOptions
{
    /// <summary>Создаёт настройки с портом 993.</summary>
    public ImapOptions() => Port = 993;

    /// <summary>Папка для запросов без явной папки.</summary>
    public string DefaultFolder { get; set; } = "INBOX";
}
