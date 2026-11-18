namespace HhAutoApplier.Domain.Links;

// Итог проверки направления связи (#132)
public enum LinkState
{
    Ok = 0,
    // До прокси не достучаться: порт не отвечает, мост или xray лежат
    ProxyUnreachable = 1,
    // Прокси ответил 502 на CONNECT: узел VPN не довёз до сервиса (случай 2026-11-18 с Telegram)
    BadGateway = 2,
    // Ответа нет за отведённое время (в том числе 504 от прокси)
    Timeout = 3,
    // Иное: отказ в подключении, другой код прокси, TLS не сложился
    Error = 4,
}

// Проверка одного направления: Link — имя для людей (Telegram, api.groq.com) или Proxy для самого прокси
public sealed record LinkProbe(string Link, bool ViaProxy, LinkState State, string? Detail = null)
{
    // Имя проверки самого прокси: порт отвечает
    public const string Proxy = "proxy";

    // Направление Bot API: по его успеху досылаются уведомления, ждущие связи
    public const string Telegram = "Telegram";

    public bool Ok => State == LinkState.Ok;
}
