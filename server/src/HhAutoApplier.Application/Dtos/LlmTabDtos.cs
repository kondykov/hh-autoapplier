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

// «LLM → Журнал» (#127): Total — вызовов по фильтру, Errors — из них с ошибкой, Matched — строк после свёртки повторов;
// Purposes и Models — для чипов фильтра (по всем вызовам, которые видно, без фильтра)
public record LlmCallsDto(int Total, int Errors, IReadOnlyList<LlmCallEntryDto> Items, int Matched, IReadOnlyList<LlmFacetDto> Purposes,
    IReadOnlyList<LlmFacetDto> Models);

public record LlmFacetDto(string Id, int Count);

// Строка журнала: Id и At — последнего вызова, Since и Count — свёрнутые повторы одной ошибки подряд (Count 1 — без свёртки).
// Outcome: ok, rejected (ответ не принят), error (ответа не было); Detail — итог оценки («подходит 82») или текст ошибки; ErrorKind — LlmErrorKinds.
// User — только администратору
public record LlmCallEntryDto(long Id, DateTimeOffset At, DateTimeOffset Since, int Count, IReadOnlyList<string> Purposes, string Model, string? Connection,
    string Outcome, string? Detail, string? ErrorKind, int PromptTokens, int CompletionTokens, string? User, string? VacancyId, string? Vacancy);

// Вызов целиком: промпт и ответ — как отправлены и получены, показывать только текстом
public record LlmCallDto(long Id, DateTimeOffset At, string Purpose, string Model, string? Connection, string? Error, string Prompt, string? Response,
    int PromptTokens, int CompletionTokens, string? VacancyId);

// «LLM → Письма» (#127): письма LLM, отклонённые проверкой (отклик на проверке с причиной «письмо: проверить — …»).
// Letter — черновик из журнала (не принят) или новое письмо, если его уже переписали (Rewritten); Model — модель черновика
public record LlmLetterDto(string VacancyId, string Title, string Company, string? Url, string Reason, string? Letter, string? Model, bool Rewritten,
    DateTimeOffset? At);

// «LLM → Подключения» (#127, только администратору): подключения сервера без учётных данных. Key — маска «…abcd» или «задан», null — не задан;
// Host — адрес провайдера без пути; Proxy — общий прокси сервера без учётных данных (#132)
public record LlmServerConnectionsDto(IReadOnlyList<LlmServerConnectionDto> Connections, IReadOnlyList<LlmTaskDefaultDto> Defaults, string? Proxy);

// Kind: main — основной провайдер, route — маршрут задачи (#104), named — подключение для выбора пользователем (#107)
public record LlmServerConnectionDto(string Kind, LlmConnectionDto Connection, string? Host, string? Key);
