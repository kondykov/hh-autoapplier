namespace HhAutoApplier.Application.Dtos;

// Дашборд профиля (#121): данные пользователя за период — 7, 30 или 90 дней. Source — выбранный агрегатор, "all" — все
public record DashboardDto(
    int Period,
    string Source,
    DateTimeOffset From,
    DateTimeOffset To,
    DashboardTotalsDto Totals,
    IReadOnlyList<DashboardFunnelStepDto> Funnel,
    DashboardSeriesDto Series,
    IReadOnlyList<DashboardSearchDto> BySearch,
    IReadOnlyList<DashboardReasonDto> Reasons,
    IReadOnlyList<DashboardSourceDto> Sources,
    DashboardLlmDto Llm,
    DashboardChatsDto Chats);

// Ключевые числа за период и за прошлый период той же длины (Prev*): собрано, отправлено, приглашений (приглашение
// или сразу собеседование), собеседований и отказов — по времени события
public record DashboardTotalsDto(
    int Collected, int PrevCollected,
    int Sent, int PrevSent,
    int Invited, int PrevInvited,
    int Interviews, int PrevInterviews,
    int Rejected, int PrevRejected);

// Шаг воронки по вакансиям, собранным за период. Base — от какого шага процент: предыдущий; у отказа — «отправлено»
public record DashboardFunnelStepDto(string Key, string Label, int Count, string? Base);

// Отклики по дням (за 90 дней — по неделям): Bucket — дней в столбце; начало столбца — полночь по Москве
public record DashboardSeriesDto(int Bucket, IReadOnlyList<DashboardPointDto> Points);

// Answered — ответы работодателя (приглашение, собеседование или отказ); Invited — приглашения, Interviews — собеседования
public record DashboardPointDto(DateTimeOffset At, int Sent, int Answered, int Invited, int Interviews);

// По поискам: собранные за период и что с ними стало
public record DashboardSearchDto(string Name, int Collected, int Sent, int Answered, int Invited, int Interviews);

public record DashboardReasonDto(string Reason, int Count);

// Разрез по агрегатору (#32): пока только hh
public record DashboardSourceDto(string Source, int Collected, int Sent, int Invited, int Interviews);

// LLM пользователя за сутки (с 03:00 МСК, как лимит): запросы, дошедшие до модели, ошибки и дневная доля
public record DashboardLlmDto(int Requests, int Errors, int Limit);

// Свежесть ответов работодателей: чаты приходят только синхронизацией в расширении. Время синхронизации не хранится —
// SyncedAt — когда сервер последний раз получил новое сообщение работодателя (синхронизация без новых сообщений его не двигает)
public record DashboardChatsDto(DateTimeOffset? SyncedAt, int Messages);

// Система (#121, только администратор): сборка, сбор, LLM сервера, пользователи, уведомления
public record SystemDashboardDto(
    int Period,
    BuildVersionDto Version,
    CollectionStateDto Collection,
    LlmOverviewDto Llm,
    IReadOnlyList<SystemUserDto> Users,
    SystemNotificationsDto Notifications);

// Пользователь за период: собрано и отправлено, расход LLM; последняя отправка и последнее сообщение из чатов
public record SystemUserDto(
    Guid Id, string Email, string Role, bool Blocked,
    int Collected, int Sent, int LlmRequests, int LlmErrors, long LlmTokens,
    DateTimeOffset? LastSentAt, DateTimeOffset? ChatsSyncedAt);

// Неотправленные уведомления Telegram: всего, из них со сбоем отправки, самое старое
public record SystemNotificationsDto(int Unsent, int Failed, DateTimeOffset? OldestUnsentAt);
