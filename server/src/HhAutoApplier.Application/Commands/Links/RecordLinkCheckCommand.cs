using HhAutoApplier.Domain.Links;

namespace HhAutoApplier.Application.Commands.Links;

// Итог проверки связи (#132) от фоновой службы: обновить состояние и журнал сбоев
public record RecordLinkCheckCommand(IReadOnlyList<LinkProbe> Probes);

// Started/Ended — сбои, открытые и закрытые этой проверкой (для журнала приложения)
public record LinkCheckResult(LinkVerdict Verdict, IReadOnlyList<LinkFailure> Started, IReadOnlyList<string> Ended, bool TelegramOk);
