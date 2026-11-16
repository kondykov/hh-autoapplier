using MimeKit;

namespace Postbox;

/// <summary>Адрес почты с необязательным отображаемым именем.</summary>
/// <param name="Address">Адрес, например <c>user@example.com</c>.</param>
/// <param name="Name">Отображаемое имя, например «Служба поддержки».</param>
public sealed record EmailAddress(string Address, string? Name = null)
{
    /// <summary>Разбирает <c>user@example.com</c> или <c>Имя &lt;user@example.com&gt;</c>.</summary>
    /// <exception cref="FormatException">Строка не похожа на адрес.</exception>
    public static EmailAddress Parse(string value)
    {
        if (!TryParse(value, out var address))
            throw new FormatException($"Не удалось разобрать адрес почты: '{value}'");
        return address;
    }

    /// <summary>Пробует разобрать адрес; <c>false</c> — строка не похожа на адрес.</summary>
    public static bool TryParse(string? value, out EmailAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(value) || !MailboxAddress.TryParse(value, out var mailbox) || !IsAddress(mailbox.Address))
            return false;
        address = From(mailbox);
        return true;
    }

    /// <summary>Строка вида <c>user@example.com</c> становится адресом.</summary>
    public static implicit operator EmailAddress(string value) => Parse(value);

    /// <inheritdoc />
    public override string ToString() => Name is null ? Address : $"{Name} <{Address}>";

    internal static bool IsAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;
        var at = address.IndexOf('@');
        return at > 0 && at < address.Length - 1 && address.IndexOf('@', at + 1) < 0 && !address.Any(char.IsWhiteSpace);
    }

    internal MailboxAddress ToMailbox() => new(Name ?? string.Empty, Address);

    internal static EmailAddress From(MailboxAddress mailbox) =>
        new(mailbox.Address, string.IsNullOrEmpty(mailbox.Name) ? null : mailbox.Name);
}
