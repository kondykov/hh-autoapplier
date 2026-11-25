// Ответы API, которые читает веб (#130). У расширения свой клиент (extensions/hh/src/api) с типами только своих запросов:
// общих типов с вебом у него почти нет, поэтому пакета packages/api при #131 не заводили. Поля — как в JSON контроллеров backend (camelCase, enum строкой)

export type VacancyStatus = "New" | "Pending" | "Review" | "Questionnaire" | "Queued" | "Applied" | "Invited" | "Interview" | "Rejected" | "Skipped" | "Failed";

export interface StatusChange {
  at: string;
  to: string;
  reason?: string | null;
}

// Просьба работодателя к письму (#72)
export interface LetterRequest {
  type: string;
  text?: string | null;
  value?: string | null;
  answer?: string | null;
  problem?: string | null;
  closed: boolean;
}

// Вопрос анкеты с ответом LLM (#58, #115)
export interface Question {
  question: string;
  type: "Text" | "Radio" | "Checkbox" | "Unknown" | string;
  options: string[];
  answer: string[];
}

export interface Vacancy {
  duplicateOfVacancyId?: string;
  otherRegions?: { id: string; region: string; url: string }[];
  id: string;
  title: string;
  url: string;
  company: string;
  area?: string | null;
  salaryFrom?: number | null;
  salaryTo?: number | null;
  salaryCurrency?: string | null;
  workFormat?: string | null;
  workExperience?: string | null;
  score: number;
  llmScore?: number | null;
  llmReason?: string | null;
  resumeProfile?: string | null;
  searchName?: string | null;
  source?: string | null;
  status: VacancyStatus | string;
  statusReason?: string | null;
  appliedAt?: string | null;
  // Число откликов на вакансию при сборе и перед своим откликом (#148); null — нет данных
  responsesAtCollect?: number | null;
  responsesAtApply?: number | null;
  collectedAt?: string | null;
  lastSeenAt?: string | null;
  updatedAt?: string | null;
  hiddenAt?: string | null;
  vacancyArchived?: boolean;
  dismissed?: boolean;
  coverLetter?: string | null;
  coverLetterModel?: string | null;
  history?: StatusChange[];
  letterRequests?: LetterRequest[];
  letterRequestsState?: string | null;
  questions?: Question[];
  stack?: string[];
  description?: string | null;
  hasTest?: boolean;
}

export type Stats = Partial<Record<VacancyStatus, number>>;

export interface Dialog {
  chatId: number | string;
  title?: string | null;
  company?: string | null;
  at: string;
  text: string;
  kind?: "Invitation" | "Question" | "Rejection" | "Other" | null;
  summary?: string | null;
  suggestedReply?: string | null;
  llmModel?: string | null;
  hhState?: string | null;
  autoReply?: "Pending" | "Sent" | "Unavailable" | null;
  autoReplyError?: string | null;
  handledAt?: string | null;
}

export interface SearchRules {
  includeKeywords?: string[];
  excludeKeywords?: string[];
  levelKeywords?: string[];
  allowedFormats?: string[];
  minSalaryRub?: number | null;
  minScore?: number;
  requireSalary?: boolean;
  skipWithTest?: boolean;
  skipReview?: boolean;
}

export interface Search {
  id: string;
  name: string;
  query: string;
  area?: string | null;
  extraParams?: string | null;
  enabled: boolean;
  rules?: SearchRules;
}

export interface Resume {
  name: string;
  match: string;
  keywords?: string[];
  summary?: string | null;
  letterTemplate?: string | null;
}

export interface Profile {
  general?: string | null;
  skills: { skill: string; facts: string }[];
  desiredSalaryRub?: number | null;
  readyForTestTask?: boolean | null;
}

export interface Me {
  id: string;
  name: string;
  role: "Admin" | "User" | string;
  viaApiKey?: boolean;
}

export interface User {
  id: string;
  email: string;
  role: string;
  blocked: boolean;
  createdAt: string;
}

export interface ApiToken {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  revokedAt?: string | null;
  // Только в ответе на выпуск
  value?: string;
}

export interface Telegram {
  configured: boolean;
  linked: boolean;
  username?: string | null;
  linkedAt?: string | null;
}

export interface TelegramLink {
  code: string;
  link?: string | null;
}

export interface CollectCounts {
  seen: number;
  added: number;
  queued: number;
  skipped: number;
}

export interface CollectState {
  current?: (CollectCounts & { trigger: string; startedAt: string; search?: string | null; page?: number }) | null;
  last?: (CollectCounts & { trigger: string; finishedAt: string; error?: string | null; searches?: number }) | null;
}

// Подключения LLM сервера для выбора пользователем (#107): без ключей и адресов. dailyLimit 0 — без ограничения
export interface LlmConnection {
  name: string;
  provider: string;
  model: string;
  available: boolean;
  pausedUntil?: string | null;
  pauseReason?: string | null;
  usedToday: number;
  dailyLimit: number;
}

export interface LlmProviders {
  providers: LlmConnection[];
  // Чем сервер выполняет задачу без выбора пользователя
  defaults: { task: string; connection: LlmConnection }[];
}

// Задача → имя подключения; null — как на сервере
export interface UserLlmSettings {
  choices: Record<string, string | null>;
}

// Вкладка LLM (#127): GET /api/llm/overview
export interface LlmOverview {
  requests: { server: number; limit: number; own: number; userLimit: number; byUser: { userId: string | null; name: string; count: number }[] | null };
  errors: { total: number; limit: number; network: number; provider: number; other: number };
  rejects: { rejected: number; evaluated: number; returned: number; borderline: number };
  letters: { rejected: number; written: number };
  routes: { task: string; choice: string | null; connection: LlmConnection; calls: number; errors: number }[];
  attention: { kind: string; text: string; href: string | null }[];
}

export interface LlmRejectItem {
  id: string;
  title: string;
  company: string;
  url: string | null;
  score: number | null;
  reason: string | null;
  group: string;
  evaluatedAt: string | null;
}

// GET /api/llm/rejects
export interface LlmRejects {
  total: number;
  borderline: number;
  groups: { id: string; title: string; count: number }[];
  items: LlmRejectItem[];
  matched: number;
  returned: { returned: number; applied: number; invited: number };
}

// POST /api/vacancies/bulk
export interface BulkResult {
  done: string[];
  failed: { id: string; error: string }[];
  // Переоценка (#147): не переоценены — вакансия в архиве или её страницы на hh.ru нет; старый backend полей не отдаёт
  archived?: string[];
  unavailable?: string[];
}

// GET /api/llm/calls (#127): count > 1 — свёрнутые повторы одной ошибки подряд (since — первый из них)
export interface LlmCallEntry {
  id: number;
  at: string;
  since: string;
  count: number;
  purposes: string[];
  model: string;
  connection: string | null;
  outcome: "ok" | "rejected" | "error";
  detail: string | null;
  errorKind: string | null;
  promptTokens: number;
  completionTokens: number;
  // Только администратору
  user: string | null;
  vacancyId: string | null;
  vacancy: string | null;
}

export interface LlmCalls {
  total: number;
  errors: number;
  items: LlmCallEntry[];
  matched: number;
  purposes: { id: string; count: number }[];
  models: { id: string; count: number }[];
}

// GET /api/llm/calls/{id}: промпт и ответ — только текстом
export interface LlmCall {
  id: number;
  at: string;
  purpose: string;
  model: string;
  connection: string | null;
  error: string | null;
  prompt: string;
  response: string | null;
  promptTokens: number;
  completionTokens: number;
  vacancyId: string | null;
}

// GET /api/llm/letters: rewritten — письмо уже переписано и ждёт одобрения в «Проверке»
export interface LlmLetter {
  vacancyId: string;
  title: string;
  company: string;
  url: string | null;
  reason: string;
  letter: string | null;
  model: string | null;
  rewritten: boolean;
  at: string | null;
}

// GET /api/llm/connections (администратор): key — маска «…abcd» или «задан», null — не задан
export interface LlmServerConnections {
  connections: { kind: "main" | "route" | "named"; connection: LlmConnection; host: string | null; key: string | null }[];
  defaults: { task: string; connection: LlmConnection }[];
  proxy: string | null;
}

// GET /api/health/links (#132)
export interface LinkOutage {
  id: number;
  subject: string;
  scope: string;
  reason: string;
  detail: string | null;
  startedAt: string;
  lastSeenAt: string;
  endedAt: string | null;
}

export interface Links {
  enabled: boolean;
  proxy: string | null;
  checkedAt: string | null;
  links: { link: string; viaProxy: boolean; state: string; detail: string | null; checkedAt: string; lastOkAt: string | null }[];
  open: LinkOutage[];
  recent: LinkOutage[];
  waitingNotifications: number;
}

// Дашборд (#121): GET /api/dashboard?period=&source=&userId=
export interface DashboardFunnelStep {
  key: string;
  label: string;
  count: number;
  // От какого шага процент; null — 100%
  base: string | null;
}

export interface DashboardPoint {
  at: string;
  sent: number;
  answered: number;
  invited: number;
  interviews: number;
}

export interface DashboardSearch {
  name: string;
  collected: number;
  sent: number;
  answered: number;
  invited: number;
  interviews: number;
}

// sent = invited + rejected + silent
export interface CompetitionRow {
  bucket: string;
  sent: number;
  invited: number;
  rejected: number;
  silent: number;
}

export interface Dashboard {
  period: number;
  source: string;
  from: string;
  to: string;
  totals: {
    collected: number; prevCollected: number; sent: number; prevSent: number; invited: number; prevInvited: number;
    interviews: number; prevInterviews: number; rejected: number; prevRejected: number;
  };
  funnel: DashboardFunnelStep[];
  series: { bucket: number; points: DashboardPoint[] };
  bySearch: DashboardSearch[];
  reasons: { reason: string; count: number }[];
  sources: { source: string; collected: number; sent: number; invited: number; interviews: number }[];
  llm: { requests: number; errors: number; limit: number };
  // syncedAt — когда пришло последнее новое сообщение работодателя (время синхронизации сервер не хранит)
  chats: { syncedAt: string | null; messages: number };
  // Исходы отправленных за период по числу откликов на вакансию (#148); последняя строка — «нет данных»
  competition?: { atCollect: CompetitionRow[]; atApply: CompetitionRow[]; growth: CompetitionRow[] };
}

// GET /api/dashboard/system — только администратору
export interface SystemUser {
  id: string;
  email: string;
  role: string;
  blocked: boolean;
  collected: number;
  sent: number;
  llmRequests: number;
  llmErrors: number;
  llmTokens: number;
  lastSentAt: string | null;
  chatsSyncedAt: string | null;
}

export interface SystemDashboard {
  period: number;
  version: { version: string; commit: string; apiVersion: number; stage?: string | null };
  collection: CollectState;
  llm: LlmOverview;
  users: SystemUser[];
  notifications: { unsent: number; failed: number; oldestUnsentAt: string | null };
}
