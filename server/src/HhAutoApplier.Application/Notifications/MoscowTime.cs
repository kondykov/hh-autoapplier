namespace HhAutoApplier.Application.Notifications;

// Время в текстах для людей — московское с пометкой (#108): «до 08:15 UTC» в 11:13 по Москве читалось как протухшее.
// UTC+3 без перевода часов, как сутки дневного лимита в расширении
public static class MoscowTime
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(3);

    public static string Format(DateTimeOffset time) => $"{time.ToOffset(Offset):HH:mm} МСК";
}
