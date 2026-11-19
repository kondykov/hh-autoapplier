// Шапка и палитра команд (#128): разделы, знак «!» у LLM, пункты палитры Ctrl+K. Без побочных эффектов при импорте —
// каркас (main.ts) передаёт сюда права, счётчики и действия, тесты — свои
import type { LlmOverview, Vacancy } from "./api/types";
import { features } from "./config";
import { LLM_SUBS } from "./pages/llm";
import { path, type RouteName } from "./router";
import type { IconName, NavAlert, PaletteGroup, PaletteItem, PaletteSource } from "./ui/ui";

// Разделы (#120): tab — место в нижней панели телефона (остальные — в «Ещё»), icon — значок там же.
// Разделы после ревизии (#128): «Обзор» (/overview) и витрина /kit (#129) — только по адресу; аккаунт (/account) — из меню
// под именем (на телефоне — в «Ещё»); «Система» — только администратору
export interface Section {
  route: RouteName;
  title: string;
  icon?: IconName;
  tab?: boolean;
  count?: "review" | "dialogs";
  admin?: boolean;
}
export const SECTIONS: Section[] = [
  { route: "dashboard", title: "Дашборд", icon: "dashboard", tab: true },
  { route: "review", title: "Проверка", icon: "review", tab: true, count: "review" },
  { route: "dialogs", title: "Ответы", icon: "dialogs", tab: true, count: "dialogs" },
  { route: "vacancies", title: "Вакансии", icon: "vacancies", tab: true },
  // LLM (#127) — по концепту после «Вакансий»; на телефоне — в «Ещё»
  { route: "llm", title: "LLM", icon: "llm" },
  { route: "searches", title: "Поиски", icon: "search" },
  { route: "candidate", title: "Кандидат", icon: "resume" },
  { route: "system", title: "Система", icon: "admin", admin: true },
];

export const visibleSections = (isAdmin: boolean) => SECTIONS.filter((s) => !s.admin || isAdmin);

// «!» у LLM: в «Требует внимания» обзора есть пауза провайдера, сбой связи (#132) или исчерпанный лимит.
// Сбой связи и лимит — bad, одна пауза — warn. Отсев и письма — не тревога (они и так в счётчиках вкладки)
const ALERT_KINDS = ["pause", "link", "limit"];
export function llmAlert(overview: Pick<LlmOverview, "attention"> | null | undefined): NavAlert | null {
  const items = (overview?.attention ?? []).filter((a) => ALERT_KINDS.includes(a.kind));
  if (!items.length) return null;
  return { tone: items.some((a) => a.kind !== "pause") ? "bad" : "warn", title: items.map((a) => a.text).join("; ") };
}

export interface CommandActions {
  theme: { label: string; icon: IconName; run: () => unknown };
  logout: () => unknown;
  collect: () => unknown;
  checkLlm: () => unknown;
}

// Группы палитры: «Разделы» (разделы и подвкладки LLM по правам, аккаунт) и «Команды» (тема, выход; сбор и проверка LLM —
// администратору). «Вакансии» — не группа, а источник с запросом (vacancySource), и только при включённом флаге
export function paletteGroups(isAdmin: boolean, actions: CommandActions): PaletteGroup[] {
  const sections: PaletteItem[] = [];
  for (const s of visibleSections(isAdmin)) {
    sections.push({ label: s.title, icon: s.icon, href: path(s.route), keywords: s.route });
    if (s.route === "llm")
      // «Обзор» — то же, что сам раздел LLM
      for (const sub of LLM_SUBS.filter((x) => (!x.admin || isAdmin) && !x.soon && x.id !== "overview"))
        sections.push({ label: `LLM · ${sub.label}`, icon: "llm", href: path("llm", {}, sub.id), keywords: sub.id });
  }
  sections.push({ label: "Аккаунт", icon: "user", href: path("account"), keywords: "пароль токены telegram" });
  const commands: PaletteItem[] = [
    { label: actions.theme.label, icon: actions.theme.icon, hint: "сменить", onRun: actions.theme.run, keywords: "тема тёмная светлая" },
    ...(isAdmin ? [
      { label: "Запустить сбор вакансий", icon: "download", hint: "администратор", onRun: actions.collect, keywords: "собрать" } as PaletteItem,
      { label: "Проверить LLM", icon: "llm", hint: "один запрос лимита", onRun: actions.checkLlm, keywords: "связь проверка" } as PaletteItem,
    ] : []),
    { label: "Выйти", icon: "logout", onRun: actions.logout, keywords: "выход" },
  ];
  return [{ title: "Разделы", items: sections }, { title: "Команды", items: commands }];
}

// Поиск по вакансиям в палитре — за флагом features.vacancySearch (по умолчанию выключен).
// ВРЕМЕННО: в GET /api/vacancies нет поиска по названию (search — имя поиска hh.ru), поэтому источник берёт одну страницу
// вакансий (load) и фильтрует её на клиенте — дальше первых take вакансий не ищет. Для релиза: серверный поиск по названию
// и компании (#128) или флаг остаётся выключенным
export const VACANCY_SEARCH_TAKE = 200;
export function vacancySource(load: () => Promise<Vacancy[]>, enabled = features.vacancySearch): PaletteSource[] {
  if (!enabled) return [];
  let cache: Promise<Vacancy[]> | null = null;
  return [{
    title: "Вакансии",
    min: 2,
    search: async (query) => {
      cache ??= load().catch((e) => { cache = null; throw e; });
      const words = query.toLocaleLowerCase("ru-RU").split(/\s+/).filter(Boolean);
      return (await cache)
        .filter((v) => { const hay = `${v.title} ${v.company}`.toLocaleLowerCase("ru-RU"); return words.every((w) => hay.includes(w)); })
        .slice(0, 8)
        .map((v): PaletteItem => ({ label: v.title, hint: v.company, icon: "external", href: v.url, external: true }));
    },
  }];
}
