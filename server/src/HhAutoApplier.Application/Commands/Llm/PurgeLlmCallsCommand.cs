namespace HhAutoApplier.Application.Commands.Llm;

// Очистка журнала LlmCalls (#113): записи старше срока хранения удаляются. Срок — 90 дней (решение владельца 2026-11-19):
// лимиты, квоты и повтор писем считаются за сутки, старые записи нужны только для разбора
public record PurgeLlmCallsCommand
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
}
