namespace HhAutoApplier.Infrastructure.Links;

// Проверка связи по направлениям (#132). Интервалы — решение владельца 2026-11-18: 2 минуты, при сбое — 30 с
public class LinkOptions
{
    public const string Section = "Links";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 120;

    public int FailureIntervalSeconds { get; set; } = 30;

    // На одно направление: подключение, CONNECT и TLS-рукопожатие вместе
    public int TimeoutSeconds { get; set; } = 10;
}
