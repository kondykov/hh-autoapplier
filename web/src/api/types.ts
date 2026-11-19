// Ответы API, которые читает веб (#130). У расширения свой клиент (src/api) с типами только своих запросов:
// общий пакет типов — при реорганизации репозитория (#131). Поля — как в JSON контроллеров backend (camelCase, enum строкой)

export type VacancyStatus = "New" | "Pending" | "Review" | "Questionnaire" | "Queued" | "Applied" | "Invited" | "Rejected" | "Skipped" | "Failed";

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
}
