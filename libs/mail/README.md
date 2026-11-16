# Postbox — почтовый сервис для .NET

> Рабочее имя. Окончательное имя пакета и пространства имён ещё не выбрано (#109); переименование — замена `Postbox` по папке.

Небольшая библиотека: **отправка писем по SMTP** и **чтение ящика по IMAP** на [MailKit](https://github.com/jstedfast/MailKit). Настройки через Options, регистрация в DI одной строкой, ошибки с понятной причиной и повторы при временных сбоях.

Библиотека знает только почту. Шаблонов, очередей, хранения отправленного и сценариев приложений («передать показания», «оповестить администратора») в ней нет — это слой приложения.

- .NET 8 и .NET 10
- Зависимости: MailKit, `Microsoft.Extensions.{Options.ConfigurationExtensions, DependencyInjection.Abstractions, Logging.Abstractions}`

## Быстрый старт

```json
// appsettings.json
{
  "Mail": {
    "Smtp": { "Host": "smtp.yandex.ru", "Port": 465, "UserName": "me@yandex.ru", "DefaultFrom": "Я <me@yandex.ru>" },
    "Imap": { "Host": "imap.yandex.ru", "UserName": "me@yandex.ru" }
  }
}
```
Пароли — в секретах или переменных окружения (`Mail__Smtp__Password`, `Mail__Imap__Password`), не в appsettings.

```csharp
builder.Services.AddMail(builder.Configuration.GetSection("Mail")); // Smtp → IEmailSender, Imap → IMailboxReader
```

### Отправка
```csharp
var result = await sender.SendAsync(new EmailMessage
{
    To = { "office@example.com" },
    Subject = "Показания счётчиков за 11.2026",
    TextBody = "Холодная вода: 123",
    HtmlBody = "<p>Холодная вода: <b>123</b></p>",          // необязательно; вместе с TextBody — альтернативные части
    Attachments = { await EmailAttachment.FromFileAsync("meter.jpg") },
    Headers = { ["X-Request-Id"] = "42" },
});
// result.MessageId, result.ServerResponse, result.Attempts, result.RejectedRecipients
```
Картинка внутри HTML: `EmailAttachment.Inline("logo.png", bytes, "logo")` и `<img src="cid:logo">`.

### Чтение
```csharp
var unread = await reader.SearchAsync(new MailSearch { UnreadOnly = true, From = "bank", Limit = 20 });
foreach (var summary in unread)
{
    var email = await reader.GetAsync(summary.Id);   // тексты и вложения; прочитанным не помечает
    // email.TextBody, email.HtmlBody, email.Attachments[i].Content ...
    await reader.SetReadAsync(summary.Id);           // или MoveAsync(id, "Processed"), DeleteAsync(id)
}
var folders = await reader.GetFoldersAsync();        // имя, назначение (Inbox/Sent/Trash…), всего и непрочитанных
```
`EmailId` — папка, её UIDVALIDITY и UID письма; строковый вид `UIDVALIDITY:UID:Папка`, обратно — `EmailId.Parse`. Можно хранить между запусками: если папку пересоздали на сервере, операция вернёт `NotFound`, а не чужое письмо.

### Без DI
```csharp
var sender = new SmtpEmailSender(new SmtpOptions { Host = "smtp.mail.ru", Port = 465, UserName = "...", Password = "...", DefaultFrom = "..." });
var reader = new ImapMailboxReader(new ImapOptions { Host = "imap.mail.ru", UserName = "...", Password = "..." });
```

## Настройки

Общие для `Smtp` и `Imap`:

| Поле | По умолчанию | Смысл |
|---|---|---|
| `Host` | — | сервер, обязательно |
| `Port` | SMTP 587, IMAP 993 | |
| `Security` | `Auto` | `Auto` — по порту (465/993 — TLS сразу, иначе STARTTLS, если сервер его предлагает), `SslOnConnect`, `StartTls`, `None` (только локальные серверы) |
| `UserName`, `Password` | пусто | задаются вместе; пусто — без входа |
| `TimeoutSeconds` | 30 | ожидание ответа на команду |
| `MaxAttempts` | 3 | попыток при временных ошибках; 1 — без повторов |
| `RetryDelayMilliseconds` | 1000 | пауза перед первым повтором, дальше удваивается |

Только `Smtp`: `DefaultFrom` — отправитель писем без `From`. Только `Imap`: `DefaultFolder` (`INBOX`).

Настройки проверяются при старте хоста (`ValidateOnStart`): все ошибки сразу, одним исключением.

Типовые серверы (у Яндекса, Mail.ru и Gmail нужен **пароль приложения**, а не пароль от ящика):

| | SMTP | IMAP |
|---|---|---|
| Яндекс | `smtp.yandex.ru:465` | `imap.yandex.ru:993` |
| Mail.ru | `smtp.mail.ru:465` | `imap.mail.ru:993` |
| Gmail | `smtp.gmail.com:587` | `imap.gmail.com:993` |

## Ошибки

Все ошибки почты — `MailException` с `Kind`:

| `Kind` | Когда | Повтор |
|---|---|---|
| `InvalidMessage` | нет адресатов или отправителя, неверный адрес, служебный заголовок в `Headers` | нет |
| `Authentication` | неверный логин или пароль | нет |
| `Tls` | не удалось защищённое соединение | нет |
| `Connection` | сервер недоступен, соединение оборвалось | да |
| `Timeout` | сервер не ответил вовремя | да |
| `Rejected` | сервер отказал: SMTP 4xx — временно (повтор), 5xx — окончательно | 4xx |
| `NotFound` | нет папки или письма | нет |
| `Protocol` | сервер ответил не по протоколу | да |

`IsTransient` — временная ли ошибка, `Attempts` — сколько попыток сделано. Повторы по настройкам к этому моменту уже выполнены. Отмена через `CancellationToken` пробрасывается как `OperationCanceledException`.

## Ограничения первой версии

- Каждая операция — своё соединение. Подходит для уведомлений и периодического опроса, не для рассылок на тысячи адресов и не для постоянного слежения за ящиком (IMAP IDLE).
- Если соединение оборвалось уже после передачи письма, но до ответа сервера, повтор может отправить письмо второй раз.
- `DeleteAsync` удаляет насовсем. Если сервер не умеет UIDPLUS, вычищаются все письма папки с пометкой «удалено». Чтобы письмо можно было восстановить, перенесите его в корзину через `MoveAsync`.
- Библиотека не создаёт папки.

## Для тестов приложений

`InMemoryEmailSender` проверяет письмо так же, как SMTP, и складывает его в `Sent`. `NullEmailSender` проверяет и выбрасывает письмо — для окружений, где почта выключена.

```csharp
services.AddSingleton<IEmailSender, InMemoryEmailSender>();
```

## Разработка

```powershell
dotnet test Postbox.slnx                     # нужен Docker: тесты поднимают GreenMail (SMTP+IMAP) в Testcontainers
dotnet pack src/Postbox -c Release -o artifacts
docker compose up -d                         # локальный GreenMail для демо: SMTP :3025, IMAP :3143, demo/secret
dotnet run --project samples/Postbox.Demo -- help
```

Демо-приложение `samples/Postbox.Demo` — консоль: `send`, `readings` (пример прикладного сценария — письмо с показаниями счётчиков), `folders`, `inbox`, `read`, `mark-read`, `move`, `delete`. По умолчанию работает с локальным GreenMail. Для настоящего ящика переопределите `Mail:*` через `dotnet user-secrets` или переменные окружения.

Пакет пока нигде не публикуется. Версия — в `Directory.Build.props`, SemVer, не связана с другими проектами репозитория.
