// Демонстрационное приложение библиотеки почты: команды отправки и чтения ящика.
// Настройки — секция Mail в appsettings.json (по умолчанию — локальный GreenMail из compose.yaml),
// пароли от настоящих ящиков — в user-secrets или переменных окружения Mail__Smtp__Password.
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Postbox;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
{
    PrintHelp();
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.AddMail(builder.Configuration.GetSection("Mail"));

using var host = builder.Build();
await host.StartAsync(); // здесь проверяются настройки почты
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var command = new Command(args);
try
{
    var sender = host.Services.GetService<IEmailSender>();
    var reader = host.Services.GetService<IMailboxReader>();
    switch (command.Name)
    {
        case "send":
            await Send(Required(sender, "Smtp"), command, cts.Token);
            break;
        case "readings":
            await SendReadings(Required(sender, "Smtp"), command, cts.Token);
            break;
        case "folders":
            foreach (var folder in await Required(reader, "Imap").GetFoldersAsync(cts.Token))
                Console.WriteLine($"{folder.FullName,-30} {folder.Role,-8} всего {folder.Count,5}, непрочитанных {folder.Unread}");
            break;
        case "inbox":
            await Inbox(Required(reader, "Imap"), command, cts.Token);
            break;
        case "read":
            await Read(Required(reader, "Imap"), command, cts.Token);
            break;
        case "mark-read":
        case "mark-unread":
            await Required(reader, "Imap").SetReadAsync(command.Id(), command.Name == "mark-read", cts.Token);
            Console.WriteLine("Готово");
            break;
        case "move":
            await Required(reader, "Imap").MoveAsync(command.Id(), command.Positional(1, "папка"), cts.Token);
            Console.WriteLine("Перенесено");
            break;
        case "delete":
            await Required(reader, "Imap").DeleteAsync(command.Id(), cts.Token);
            Console.WriteLine("Удалено");
            break;
        default:
            Console.Error.WriteLine($"Неизвестная команда: {command.Name}");
            PrintHelp();
            return 2;
    }
    return 0;
}
catch (MailException e)
{
    Console.Error.WriteLine($"Ошибка почты ({e.Kind}{(e.IsTransient ? ", временная" : "")}, попыток {e.Attempts}): {e.Message}");
    return 1;
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
finally
{
    await host.StopAsync();
}

static async Task Send(IEmailSender sender, Command command, CancellationToken token)
{
    var message = new EmailMessage
    {
        Subject = command.Option("subject") ?? "",
        TextBody = command.Option("text") ?? ReadFile(command.Option("text-file")),
        HtmlBody = ReadFile(command.Option("html-file")),
    };
    foreach (var to in command.List("to"))
        message.To.Add(EmailAddress.Parse(to));
    foreach (var cc in command.List("cc"))
        message.Cc.Add(EmailAddress.Parse(cc));
    foreach (var file in command.List("attach"))
        message.Attachments.Add(await EmailAttachment.FromFileAsync(file, cancellationToken: token));
    if (command.Option("from") is { } from)
        message.From = EmailAddress.Parse(from);

    Print(await sender.SendAsync(message, token));
}

// Пример сценария приложения поверх библиотеки: письмо с показаниями счётчиков
static async Task SendReadings(IEmailSender sender, Command command, CancellationToken token)
{
    var readings = new (string Option, string Title)[]
    {
        ("cold", "Холодная вода, м³"), ("hot", "Горячая вода, м³"), ("electricity", "Электроэнергия, кВт·ч"),
    }.Where(r => command.Option(r.Option) is not null)
     .Select(r => (r.Title, Value: command.Option(r.Option)!))
     .ToList();
    if (readings.Count == 0)
        throw new ArgumentException("Нет показаний: укажите --cold, --hot или --electricity");

    var period = command.Option("period") ?? DateTime.Today.ToString("MM.yyyy");
    var text = new StringBuilder($"Показания счётчиков за {period}");
    if (command.Option("account") is { } account)
        text.Append($", лицевой счёт {account}");
    text.AppendLine(":").AppendLine();
    foreach (var (title, value) in readings)
        text.AppendLine($"{title}: {value}");

    var message = new EmailMessage { Subject = $"Показания счётчиков за {period}", TextBody = text.ToString() };
    foreach (var to in command.List("to"))
        message.To.Add(EmailAddress.Parse(to));
    foreach (var file in command.List("attach"))
        message.Attachments.Add(await EmailAttachment.FromFileAsync(file, cancellationToken: token));

    Console.WriteLine(text);
    Print(await sender.SendAsync(message, token));
}

static async Task Inbox(IMailboxReader reader, Command command, CancellationToken token)
{
    var search = new MailSearch
    {
        Folder = command.Option("folder"),
        UnreadOnly = !command.Flag("all"),
        From = command.Option("from"),
        Subject = command.Option("subject"),
        Limit = int.TryParse(command.Option("limit"), out var limit) ? limit : 20,
    };
    var found = await reader.SearchAsync(search, token);
    if (found.Count == 0)
        Console.WriteLine(search.UnreadOnly ? "Непрочитанных писем нет (--all — все письма)" : "Писем нет");
    foreach (var s in found)
    {
        var marks = (s.IsRead ? " " : "●") + (s.HasAttachments ? "📎" : "  ");
        Console.WriteLine($"{marks} {s.Date.LocalDateTime:dd.MM HH:mm}  {string.Join(", ", s.From),-35}  {s.Subject}");
        Console.WriteLine($"     id: {s.Id}");
    }
}

static async Task Read(IMailboxReader reader, Command command, CancellationToken token)
{
    var id = command.Id();
    var email = await reader.GetAsync(id, token);
    Console.WriteLine($"От:    {string.Join(", ", email.From)}");
    Console.WriteLine($"Кому:  {string.Join(", ", email.To)}");
    Console.WriteLine($"Дата:  {email.Date.LocalDateTime:dd.MM.yyyy HH:mm}");
    Console.WriteLine($"Тема:  {email.Subject}");
    Console.WriteLine();
    Console.WriteLine(email.TextBody ?? (email.HtmlBody is null ? "(пусто)" : "(только HTML)\n" + email.HtmlBody));

    if (email.Attachments.Count > 0)
    {
        Console.WriteLine();
        var directory = command.Option("save");
        foreach (var attachment in email.Attachments)
        {
            Console.WriteLine($"📎 {attachment.FileName} ({attachment.ContentType}, {attachment.Content.Length} байт{(attachment.IsInline ? ", встроено" : "")})");
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, Path.GetFileName(attachment.FileName)), attachment.Content, token);
            }
        }
        if (directory is not null)
            Console.WriteLine($"Вложения сохранены в {Path.GetFullPath(directory)}");
    }

    if (command.Flag("mark"))
        await reader.SetReadAsync(id, true, token);
}

static void Print(EmailSendResult result)
{
    Console.WriteLine($"Отправлено: {result.MessageId} (попытка {result.Attempts}), сервер: {result.ServerResponse}");
    if (result.RejectedRecipients.Count > 0)
        Console.WriteLine($"Не приняты: {string.Join(", ", result.RejectedRecipients)}");
}

static string? ReadFile(string? path) => path is null ? null : File.ReadAllText(path);

static T Required<T>(T? service, string section) where T : class =>
    service ?? throw new ArgumentException($"Не настроена секция Mail:{section}");

static void PrintHelp() => Console.WriteLine("""
    Демонстрация библиотеки почты Postbox

    Отправка (Mail:Smtp):
      send --to a@x.ru[,b@x.ru] --subject "Тема" (--text "..." | --text-file f.txt) [--html-file f.html] [--cc ...] [--attach файл]... [--from ...]
      readings --to a@x.ru [--cold 123] [--hot 45] [--electricity 678] [--account 001] [--period 11.2026] [--attach фото.jpg]

    Чтение (Mail:Imap):
      folders
      inbox [--all] [--folder INBOX] [--from ...] [--subject ...] [--limit 20]
      read <id> [--save папка] [--mark]
      mark-read <id> | mark-unread <id>
      move <id> <папка>
      delete <id>

    id — из вывода inbox. По умолчанию настройки на локальный GreenMail: docker compose up -d
    """);

// Простой разбор: позиционные аргументы и --ключ значение (повторяемые, через запятую)
internal sealed class Command
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);

    public Command(string[] args)
    {
        Name = args[0];
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = args[i][2..];
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                if (!_options.TryGetValue(key, out var values))
                    _options[key] = values = [];
                values.Add(value);
            }
            else
            {
                _positional.Add(args[i]);
            }
        }
    }

    public string Name { get; }

    public string? Option(string key) => _options.TryGetValue(key, out var values) ? values[^1] : null;

    public bool Flag(string key) => Option(key) is "true";

    public IEnumerable<string> List(string key) => _options.TryGetValue(key, out var values)
        ? values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        : [];

    public string Positional(int index, string what) =>
        index < _positional.Count ? _positional[index] : throw new ArgumentException($"Не указан аргумент: {what}");

    public EmailId Id() => EmailId.TryParse(Positional(0, "id письма"), out var id)
        ? id
        : throw new ArgumentException($"Неверный id письма: '{_positional[0]}' (берите из вывода inbox)");
}
