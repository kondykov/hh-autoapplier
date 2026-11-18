// Общая модель панели (#123): состояние, которое рисует любой вид (боковая панель Chrome, плавающая панель, плашка),
// и команды, которые вид отправляет обратно. Ничего про конкретный сайт: страницу описывает PageKind,
// перевод адреса сайта в PageKind — в коде расширения конкретного сайта

import type { LogLevel } from "../../infra/logger";

// Вид панели на странице; связь со страницей у всех одна, режим меняет только вид
export type PanelMode = "side" | "floating" | "pill";

export const PANEL_MODES: { mode: PanelMode; label: string; title: string }[] = [
  { mode: "side", label: "Боковая", title: "Боковая панель браузера: не закрывает страницу" },
  { mode: "floating", label: "Плавающая", title: "Панель поверх страницы, перетаскивается за шапку" },
  { mode: "pill", label: "Плашка", title: "Свёрнутая плашка в углу, раскрывается по клику" },
];

export const isPanelMode = (v: unknown): v is PanelMode => v === "side" || v === "floating" || v === "pill";

// none — во вкладке нет нашей страницы (другой сайт или расширение на ней не запущено)
export type PageKind = "search" | "vacancy" | "chat" | "other" | "none";

export interface LogLine {
  at: number;
  level: LogLevel;
  text: string;
}

// Журнал в панели ограничен: старые строки отбрасываются
export const LOG_LIMIT = 50;

export type ServerLinkState = "unknown" | "checking" | "ok" | "warn" | "error";

export interface ServerLink {
  state: ServerLinkState;
  // Коротко для шапки: версия сервера или «нет связи»
  label: string;
  // Полностью — во всплывающей подсказке и в журнале
  detail: string;
  contract?: number;
}

// Сводка очереди с сервера; null — ещё не известна
export interface ServerQueue {
  queued: number;
  review: number;
}

export interface PanelState {
  version: string;
  mode: PanelMode;
  page: PageKind;
  // Короткое имя страницы для шапки: «Выдача», «Вакансия», «Чаты»
  pageTitle: string;
  step: string;
  captcha: boolean;
  queue: {
    running: boolean;
    // Очередь ведёт другая вкладка: эта только показывает
    elsewhere: boolean;
    dryRun: boolean;
    runApplied: number;
    runLimit: number;
    daily: number;
    dailyLimit: number;
  };
  server: ServerLink;
  serverQueue: ServerQueue | null;
  // Поиск на сервере, куда уходит выдача: имя — на кнопке
  importSearch: string;
  thanksOnReject: boolean;
  rejectsActive: boolean;
  log: LogLine[];
}

export const emptyState = (version: string): PanelState => ({
  version,
  mode: "side",
  page: "none",
  pageTitle: "",
  step: "",
  captcha: false,
  queue: { running: false, elsewhere: false, dryRun: true, runApplied: 0, runLimit: 0, daily: 0, dailyLimit: 0 },
  server: { state: "unknown", label: "сервер", detail: "Связь с сервером не проверялась" },
  serverQueue: null,
  importSearch: "",
  thanksOnReject: true,
  rejectsActive: false,
  log: [],
});

export type PanelCommand =
  | { kind: "queue.toggle" }
  | { kind: "queue.dryRun"; enabled: boolean }
  | { kind: "search.send" }
  | { kind: "chats.sync" }
  | { kind: "server.check" }
  | { kind: "rejects.next" }
  | { kind: "rejects.auto" }
  | { kind: "mode"; mode: PanelMode }
  | { kind: "options.open" }
  | { kind: "site.open" };

// Разделы панели в порядке показа
export type PanelSection =
  | "openSite" // во вкладке нет нашей страницы: открыть сайт
  | "sendSearch" // выдача → поиск на сервере
  | "queue" // очередь: запуск, пробный режим, прогресс, лимит
  | "syncChats" // главное действие на «Чатах»
  | "syncChatsLink" // с другой страницы: открыть «Чаты» и синхронизировать
  | "oldTools"; // «Next Отказ», «Авто-отказ» — свёрнуто

// Что уместно на этой странице. Идущая очередь видна везде: её должно быть можно остановить
export function sectionsFor(page: PageKind, queueRunning = false): PanelSection[] {
  let sections: PanelSection[];
  switch (page) {
    case "search":
      sections = ["sendSearch", "queue", "syncChatsLink"];
      break;
    case "vacancy":
      sections = ["queue", "syncChatsLink"];
      break;
    case "chat":
      sections = ["syncChats", "oldTools"];
      break;
    case "other":
      sections = ["queue", "syncChatsLink"];
      break;
    case "none":
      sections = ["openSite", "syncChatsLink"];
      break;
  }
  if (queueRunning && !sections.includes("queue")) sections = page === "none" ? sections : ["queue", ...sections];
  return sections;
}

// Добавить строку журнала с ограничением длины
export function appendLog(log: LogLine[], line: LogLine, limit = LOG_LIMIT): LogLine[] {
  const next = [...log, line];
  return next.length > limit ? next.slice(next.length - limit) : next;
}
