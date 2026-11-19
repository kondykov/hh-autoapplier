namespace HhAutoApplier.Application.Dtos;

// Вкладка «LLM → Обзор» (#127)
public record LlmOverviewDto(
    LlmRequestsDto Requests,
    LlmErrorsDto Errors,
    LlmRejectsSummaryDto Rejects,
    LlmLettersSummaryDto Letters,
    IReadOnlyList<LlmRouteDto> Routes,
    IReadOnlyList<LlmAttentionDto> Attention);

// Запросы за сутки (UTC), которые расходуют лимит: Server — всего по серверу, Own — данных, которые смотрим;
// Limit — дневной лимит сервера, UserLimit — доля пользователя (0 — без ограничения); ByUser — только администратору
public record LlmRequestsDto(int Server, int Limit, int Own, int UserLimit, IReadOnlyList<LlmUserRequestsDto>? ByUser);

// UserId null — владелец по умолчанию и проверки ключа
public record LlmUserRequestsDto(Guid? UserId, string Name, int Count);

// Ошибки за 24 ч по видам (LlmErrorKinds)
public record LlmErrorsDto(int Total, int Limit, int Network, int Provider, int Other);

// Отсеяно LLM за 30 дней из оценённых; Returned — возвращены вручную (вердикт Reject, но уже не отсеяны);
// Borderline — отсеянные с баллом 60–69, не скрытые
public record LlmRejectsSummaryDto(int Rejected, int Evaluated, int Returned, int Borderline);

// Письма на проверке, отклонённые проверкой письма, и письма LLM за 30 дней
public record LlmLettersSummaryDto(int Rejected, int Written);

// Маршрут задачи: чем она выполняется для данных, которые смотрим (Choice — выбор пользователя, null — как на сервере), ошибки за 24 ч
public record LlmRouteDto(string Task, string? Choice, LlmConnectionDto Connection, int Calls, int Errors);

// Kind: pause, link, limit, rejects, letters; Href — куда перейти в вебе
public record LlmAttentionDto(string Kind, string Text, string? Href);

// Вкладка «LLM → Отсев» (#127)
public record LlmRejectsDto(int Total, int Borderline, IReadOnlyList<LlmRejectGroupDto> Groups, IReadOnlyList<LlmRejectItemDto> Items, int Matched,
    LlmReturnedDto Returned);

public record LlmRejectGroupDto(string Id, string Title, int Count);

public record LlmRejectItemDto(string Id, string Title, string Company, string? Url, int? Score, string? Reason, string Group, DateTimeOffset? EvaluatedAt);

// Итог возвратов: сколько вернули вручную, по скольким откликнулись и сколько из них пригласили
public record LlmReturnedDto(int Returned, int Applied, int Invited);

// Массовое действие (#127): что получилось и что нет — по id с причиной
public record BulkVacanciesResultDto(IReadOnlyList<string> Done, IReadOnlyList<BulkFailureDto> Failed);

public record BulkFailureDto(string Id, string Error);
