using Microsoft.Extensions.Options;

namespace Postbox.Internal;

internal sealed class MailServerOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : MailServerOptions
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var errors = new List<string>();
        var section = typeof(TOptions).Name;

        if (string.IsNullOrWhiteSpace(options.Host))
            errors.Add($"{section}.Host: не задан сервер");
        if (options.Port is < 1 or > 65535)
            errors.Add($"{section}.Port: {options.Port} вне диапазона 1–65535");
        if (options.TimeoutSeconds < 1)
            errors.Add($"{section}.TimeoutSeconds: должно быть не меньше 1");
        if (options.MaxAttempts < 1)
            errors.Add($"{section}.MaxAttempts: должно быть не меньше 1");
        if (options.RetryDelayMilliseconds < 0)
            errors.Add($"{section}.RetryDelayMilliseconds: не может быть отрицательным");
        if (string.IsNullOrEmpty(options.UserName) != string.IsNullOrEmpty(options.Password))
            errors.Add($"{section}: логин и пароль задаются вместе");

        switch (options)
        {
            case SmtpOptions { DefaultFrom: { Length: > 0 } from } when !EmailAddress.TryParse(from, out _):
                errors.Add($"{section}.DefaultFrom: '{from}' не похож на адрес почты");
                break;
            case ImapOptions imap when string.IsNullOrWhiteSpace(imap.DefaultFolder):
                errors.Add($"{section}.DefaultFolder: не задана папка");
                break;
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
