// LLM (#127): подвкладки Обзор · Отсев · Журнал · Письма · Мои модели · Подключения (администратор).
// «Мои модели» — #107, остальные — #127; «Подключения» — только администратору
import { api, put } from "../api/http";
import type { LlmConnection, LlmProviders, UserLlmSettings } from "../api/types";
import { connections } from "./llm-connections";
import { journal, letters } from "./llm-log";
import { overview, rejects } from "./llm-tab";
import { path, type Route } from "../router";
import { act, app, me, readOnly, scoped, session } from "../state";
import { Button, Card, el, EmptyState, Form, PageHead, StackTable, Tabs, Toolbar, type Child } from "@hh-autoapplier/ui";

// Задачи — как маршруты сервера (#104)
export const LLM_TASKS: [string, string][] = [
  ["Evaluation", "Оценка"], ["Letters", "Письма"], ["Questionnaires", "Анкеты"], ["Dialogs", "Ответы работодателям"],
];

interface Sub {
  id: string;
  label: string;
  admin?: boolean;
  soon?: string;
}
export const LLM_SUBS: Sub[] = [
  { id: "overview", label: "Обзор" },
  { id: "rejects", label: "Отсев" },
  { id: "journal", label: "Журнал" },
  { id: "letters", label: "Письма" },
  { id: "models", label: "Мои модели" },
  { id: "connections", label: "Подключения", admin: true },
];
const DEFAULT_SUB = "overview";

const time = (iso: string) => new Date(iso).toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });

// Состояние подключения одной строкой: пауза, исчерпанный лимит или остаток
export function connectionState(c: LlmConnection): string {
  if (c.pausedUntil) return `пауза до ${time(c.pausedUntil)}`;
  if (!c.available) return c.dailyLimit && c.usedToday >= c.dailyLimit ? "лимит исчерпан" : "недоступно";
  return c.dailyLimit ? `осталось ${Math.max(0, c.dailyLimit - c.usedToday)}` : "без лимита";
}

// Варианты выбора задачи: «как на сервере» и подключения; сохранённое, которого уже нет на сервере, — отдельной строкой
export function choiceOptions(providers: LlmConnection[], serverDefault: LlmConnection | undefined, current: string | null): [string, string][] {
  const label = (c: LlmConnection) => `${c.name} · ${c.model}${c.available ? "" : ` — ${connectionState(c)}`}`;
  const options: [string, string][] = [["", `как на сервере${serverDefault ? ` · ${serverDefault.model}` : ""}`], ...providers.map((c): [string, string] => [c.name, label(c)])];
  if (current && !providers.some((c) => c.name.toLowerCase() === current.toLowerCase())) options.push([current, `${current} — нет на сервере`]);
  return options;
}

export async function llm(route: Route): Promise<Child> {
  const isAdmin = session.me?.role === "Admin";
  const subs = LLM_SUBS.filter((s) => !s.admin || isAdmin);
  const sub = subs.find((s) => s.id === route.sub) ?? subs.find((s) => s.id === DEFAULT_SUB)!;
  const tabs = Tabs(subs.map((s) => ({ href: path("llm", {}, s.id), label: s.label, active: s.id === sub.id })), { label: "Подвкладки LLM" });
  tabs.classList.add("views");
  const head = PageHead("LLM", "Оценка вакансий, анкеты, письма и разбор ответов работодателей");
  // Подвкладка администратора по адресу у пользователя — объяснение, а не молча «Обзор»
  if (route.sub && !subs.some((s) => s.id === route.sub) && LLM_SUBS.some((s) => s.id === route.sub && s.admin))
    return [head, tabs, Card({}, EmptyState("Раздел только для администратора.", { icon: "admin" }))];
  if (sub.soon) return [head, tabs, Card({}, EmptyState(`Скоро: ${sub.soon}.`, { icon: "llm" }))];
  if (sub.id === "overview") return [head, tabs, await overview()];
  if (sub.id === "rejects") return [head, tabs, await rejects(route)];
  if (sub.id === "journal") return [head, tabs, await journal(route)];
  if (sub.id === "letters") return [head, tabs, await letters()];
  if (sub.id === "connections") return [head, tabs, await connections()];
  return [head, tabs, await models()];
}

async function models(): Promise<Child> {
  const [providers, settings] = await Promise.all([api<LlmProviders>("/api/llm/providers"), api<UserLlmSettings>(scoped("/api/profile/llm"))]);
  const owner = readOnly() ? "выбранного пользователя" : me().name;
  const selects = new Map<string, HTMLSelectElement>();

  const rows = LLM_TASKS.map(([task, title]) => {
    const serverDefault = providers.defaults.find((d) => d.task === task)?.connection;
    const current = settings.choices[task] ?? null;
    const select = el("select", { disabled: readOnly(), "aria-label": `Модель: ${title}` },
      choiceOptions(providers.providers, serverDefault, current).map(([value, textContent]) => el("option", { value, textContent })));
    select.value = current ?? "";
    selects.set(task, select);
    const state = el("span", { className: "muted" });
    const show = () => {
      const chosen = providers.providers.find((c) => c.name === select.value) ?? (select.value ? undefined : serverDefault);
      state.textContent = chosen ? connectionState(chosen) : "нет на сервере — пойдёт как на сервере";
    };
    select.onchange = show;
    show();
    return [title, select, state];
  });

  const save = Button("Сохранить", { kind: "primary", type: "submit" });
  const empty = providers.providers.length === 0;
  return Form({ submit: save, onSubmit: () => act(() => put<UserLlmSettings>("/api/profile/llm",
    { choices: Object.fromEntries([...selects].map(([task, s]) => [task, s.value || null])) }),
  () => { app.render(); return "Модели LLM сохранены"; }) },
  el("h2", { textContent: "Мои модели" }),
  el("p", { className: "muted", textContent: `Для откликов ${owner}. Модель для каждой задачи — из подключений сервера или «как на сервере»; ключей здесь нет. Разово другой моделью — «Оценить заново через» на карточке отсеянной вакансии, настройка не меняется.` }),
  empty ? EmptyState("На сервере нет подключений для выбора: все задачи идут как на сервере. Подключения задаёт администратор в .env (Llm__Providers__…).", { icon: "llm" }) : null,
  StackTable([{ title: "Задача" }, { title: "Модель" }, { title: "Остаток" }], rows),
  readOnly() || empty ? null : Toolbar([save], { after: true }));
}
