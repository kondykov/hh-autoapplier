// LLM (#127): «Обзор» — запросы и лимит, ошибки, отсев, письма, маршруты задач и «Требует внимания»;
// «Отсев» — отсеянные LLM по группам причины с выбором и массовыми действиями. Фильтры «Отсева» — в адресе
import { api, post } from "../api/http";
import type { BulkResult, LlmOverview, LlmProviders, LlmRejectItem, LlmRejects } from "../api/types";
import { path, type Route } from "../router";
import { act, app, readOnly, scoped } from "../state";
import { undoable } from "../undo";
import {
  Badge, Button, Caption, Card, Chips, Confirm, el, EmptyState, FilterList, KeyHints, Menu, Meter, Pill, PickRow, Segmented, SelectionBar, StackTable, Stat, Stats, Toast, Toolbar,
  type Child, type PickElement, type SelectionBarElement, type Tone,
} from "../ui/ui";
import { connectionState, LLM_TASKS } from "./llm";
import { isTyping } from "./review-keys";

// Больше стольких за раз — с подтверждением (решение 2026-11-19)
export const CONFIRM_OVER = 20;
const PAGE = 100;

const taskTitle = (task: string) => LLM_TASKS.find(([t]) => t === task)?.[1] ?? task;
const plural = (n: number, one: string, few: string, many: string) => {
  const d = n % 10, h = n % 100;
  return `${n} ${d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many}`;
};
export const vacancies = (n: number) => plural(n, "вакансия", "вакансии", "вакансий");

// ── Обзор ─────────────────────────────────────────────────────────────
export async function overview(): Promise<Child> {
  const o = await api<LlmOverview>(scoped("/api/llm/overview"));
  const r = o.requests;
  const users = r.byUser?.filter((u) => u.count).map((u) => `${u.name.split("@")[0]} ${u.count}`).join(", ");
  const requests = Stat({ value: r.server, unit: r.limit ? ` / ${r.limit}` : "", label: "запросов сегодня (UTC)",
    note: users ? `по пользователям: ${users}` : `ваши ${r.own}${r.userLimit ? ` из ${r.userLimit}` : ""}` });
  if (r.limit) requests.append(Meter(r.server, r.limit, { label: "Расход дневного лимита", color: r.server >= r.limit ? "var(--bad)" : r.server >= r.limit * 0.8 ? "var(--warn)" : undefined }));
  const e = o.errors;
  const tiles = Stats([
    requests,
    Stat({ value: e.total, label: "ошибок за сутки", note: e.total ? `лимит ${e.limit} · сеть ${e.network} · провайдер ${e.provider} · прочее ${e.other}` : "ошибок нет",
      trend: e.total ? "down" : null }),
    Stat({ value: o.rejects.rejected, label: "отсеяно LLM · 30 дн", note: `из ${o.rejects.evaluated} оценённых · вернули ${o.rejects.returned}` }),
    Stat({ value: o.letters.rejected, label: "писем отклонено проверкой", note: `LLM написала за 30 дн: ${o.letters.written}`, trend: o.letters.rejected ? "down" : null }),
  ]);

  const routes = Card({ title: "Маршруты по задачам" },
    StackTable([{ title: "Задача" }, { title: "Модель" }, { title: "Состояние" }, { title: "Вызовов 24 ч", num: true }, { title: "Ошибок 24 ч", num: true }],
      o.routes.map((x) => [
        taskTitle(x.task),
        el("span", {}, Pill(x.connection.model, { dot: x.connection.available ? "var(--ok)" : "var(--warn)" }), " ",
          el("span", { className: "muted small", textContent: x.choice ? `ваш выбор: ${x.connection.name}` : `как на сервере · ${x.connection.name}` })),
        connectionState(x.connection),
        x.calls,
        x.errors ? Pill(String(x.errors), { tone: "bad" }) : "0",
      ])),
    el("p", { className: "muted small" }, "Свою модель для задачи — в ", el("a", { href: path("llm", {}, "models"), textContent: "«Мои модели»" }), "."));

  const attention = Card({ title: "Требует внимания" },
    o.attention.length
      ? el("ul", { className: "reasons" }, o.attention.map((a) => el("li", {},
        el("span", {}, Badge(ATTENTION[a.kind] ?? a.kind, { tone: a.kind === "rejects" || a.kind === "letters" ? "accent" : "bad" }), a.text),
        a.href ? el("a", { href: a.href, textContent: "Открыть" }) : null)))
      : EmptyState("Всё в порядке: пауз, сбоев связи и пограничных отсеянных нет.", { icon: "check" }));

  return [tiles, el("div", { className: "cols wide-left" }, routes, attention)];
}

const ATTENTION: Record<string, string> = { pause: "пауза", link: "связь", limit: "лимит", rejects: "отсев", letters: "письма" };

// ── Отсев ─────────────────────────────────────────────────────────────
export interface RejectsFilter {
  period: string;
  group: string | null;
  border: boolean;
  n: number;
}

// Фильтры — из адреса: period=7|30|all (по умолчанию 30), group, border=1, n — сколько строк показано
export function rejectsFilter(q: URLSearchParams): RejectsFilter {
  const period = ["7", "30", "all"].includes(q.get("period") ?? "") ? q.get("period")! : "30";
  const n = Math.min(500, Math.max(PAGE, Number(q.get("n")) || PAGE));
  return { period, group: q.get("group") || null, border: q.get("border") === "1", n };
}

export function rejectsPath(f: RejectsFilter, patch: Partial<RejectsFilter> = {}): string {
  const x = { ...f, ...patch };
  return path("llm", { period: x.period === "30" ? null : x.period, group: x.group, border: x.border ? "1" : null, n: x.n > PAGE ? String(x.n) : null }, "rejects");
}

export function rejectsQuery(f: RejectsFilter): string {
  const q = new URLSearchParams({ take: String(f.n) });
  if (f.period !== "all") q.set("period", f.period);
  if (f.group) q.set("group", f.group);
  if (f.border) q.set("border", "true");
  return `/api/llm/rejects?${q}`;
}

const scoreTone = (score: number | null): Tone | undefined => (score != null && score >= 60 ? "accent" : undefined);

export async function rejects(route: Route): Promise<Child> {
  const f = rejectsFilter(route.query);
  const [data, providers] = await Promise.all([api<LlmRejects>(scoped(rejectsQuery(f))), api<LlmProviders>("/api/llm/providers").catch(() => null)]);
  const editable = !readOnly();

  const go = (patch: Partial<RejectsFilter>) => app.navigate(rejectsPath(f, { ...patch, n: PAGE }));
  const filters = Toolbar([
    Segmented([{ value: "7", label: "7 дн" }, { value: "30", label: "30 дн" }, { value: "all", label: "всё" }], f.period, (period) => go({ period }), { label: "Период" }),
    Chips([{ value: false, label: "все баллы" }, { value: true, label: `пограничные 60–69 · ${data.borderline}`, color: "var(--warn)" }], f.border, (border) => go({ border }),
      { label: "Балл" }),
  ]);

  const all = data.groups.reduce((s, g) => s + g.count, 0);
  const groups = Card({},
    Caption("Почему LLM отсеяла"),
    FilterList([{ href: rejectsPath(f, { group: null, n: PAGE }), label: "Все", count: all, active: !f.group },
      ...byCount(data.groups).map((g) => ({ href: rejectsPath(f, { group: g.id, n: PAGE }), label: g.title, count: g.count, active: f.group === g.id }))], { max: all, label: "Группы причин" }),
    el("p", { className: "muted small", textContent: `Группы — по ключевым словам причины LLM. Вернули вручную: ${data.returned.returned} (откликнулись ${data.returned.applied}, приглашений ${data.returned.invited}).` }));

  if (!data.items.length) {
    const empty = data.total ? "В этой группе отсеянных нет." : "Отсеянных LLM за период нет.";
    return [filters, el("div", { className: "cols narrow-left" }, groups, Card({}, EmptyState(empty, { icon: "check" })))];
  }

  const rows: { item: LlmRejectItem; row: PickElement }[] = [];
  const selected = () => rows.filter((r) => r.row.checked).map((r) => r.item.id);
  let bar: SelectionBarElement | null = null;
  const sync = () => bar?.update(selected().length, rows.length);
  const refresh = () => app.render();

  // Обратимое: «Вернуть» (обратное — Skip с причиной LLM) и «Скрыть» (обратное — Unhide)
  const reversible = (ids: string[], action: "Requeue" | "Hide") => undoable(() => bulk(action, ids), {
    done: (r) => { refresh(); return summary(action === "Requeue" ? "Вернули в очередь" : "Скрыли", r); },
    undo: () => bulk(action === "Requeue" ? "Skip" : "Unhide", ids),
    undone: action === "Requeue" ? "Снова в отсеве" : "Снова в списке",
    onUndone: refresh,
  });
  const reevaluate = async (ids: string[], provider?: string) => {
    const r = await act(() => bulk("Reevaluate", ids, provider));
    if (r) { Toast(summary(provider ? `Оценили заново через ${provider}` : "Отправили на оценку заново", r), { tone: r.failed.length ? "bad" : "ok" }); refresh(); }
  };
  // Больше CONFIRM_OVER — с подтверждением
  const confirmed = async (ids: string[], title: string, text: string) =>
    ids.length <= CONFIRM_OVER || await Confirm({ title: `${title}: ${vacancies(ids.length)}?`, text, confirm: title });

  const actions = (): Child[] => [
    Button("Вернуть в очередь", { kind: "primary", icon: "undo", onClick: async () => {
      const ids = selected();
      if (await confirmed(ids, "Вернуть в очередь", "Расширение откликнется на них в обычном порядке; отменить можно тостом.")) await reversible(ids, "Requeue");
    } }),
    Button("Оценить заново", { onClick: async () => {
      const ids = selected();
      if (await confirmed(ids, "Оценить заново", "Правила и затем LLM: каждая вакансия — запрос дневного лимита. Письма LLM сбросятся.")) await reevaluate(ids);
    } }),
    providers?.providers.length ? Menu("Оценить заново через", providers.providers.map((c) => ({
      label: `${c.name} · ${c.model}${c.available ? "" : ` — ${connectionState(c)}`}`,
      onClick: async () => {
        const ids = selected();
        if (await confirmed(ids, `Оценить через ${c.name}`, "Каждая вакансия — запрос к этому подключению сразу, это займёт время.")) await reevaluate(ids, c.name);
      },
    })), { button: true, label: "Оценить заново через подключение" }) : null,
    Button("Скрыть", { onClick: async () => {
      const ids = selected();
      if (await confirmed(ids, "Скрыть", "Скрытые пропадут из списка; отменить можно тостом.")) await reversible(ids, "Hide");
    } }),
  ];

  for (const item of data.items) {
    const row = PickRow({
      title: item.title,
      href: item.url ?? undefined,
      meta: [item.company, GROUP_SHORT[item.group]],
      note: el("span", {}, el("b", { textContent: "LLM: " }), item.reason ?? "без причины"),
      score: item.score,
      tone: scoreTone(item.score),
      onToggle: sync,
      actions: editable ? Button("Вернуть в очередь", { size: "sm", icon: "undo", iconOnly: true, onClick: () => reversible([item.id], "Requeue") }) : null,
    });
    row.dataset.id = item.id;
    rows.push({ item, row });
  }
  const list = el("div", { className: "picks" }, rows.map((r) => r.row));
  if (editable) {
    document.querySelectorAll<SelectionBarElement>(".selbar.llm-rejects").forEach((b) => b.dispose());
    bar = SelectionBar(actions, { onAll: (on) => { rows.forEach((r) => { r.row.checked = on; }); sync(); } });
    bar.classList.add("llm-rejects");
    bindPickKeys(list, sync);
  }
  sync();

  const more = data.matched > data.items.length && f.n < 500
    ? el("p", {}, el("a", { href: rejectsPath(f, { n: f.n + PAGE }), textContent: `Показать ещё (${data.items.length} из ${data.matched})` }))
    : el("p", { className: "muted small", textContent: `Показано ${data.items.length} из ${data.matched}` });
  return [
    filters,
    editable ? KeyHints([["J/K", "строки"], ["X", "выбрать"]]) : null,
    el("div", { className: "cols narrow-left" }, groups, Card({}, bar, list, more)),
  ];
}

const GROUP_SHORT: Record<string, string> = { stack: "стек", level: "уровень", office: "офис", notdev: "не разработка", data: "мало данных", other: "прочее" };

const bulk = (action: string, ids: string[], provider?: string) => post<BulkResult>("/api/vacancies/bulk", { action, ids, provider });

export function summary(done: string, r: BulkResult): string {
  const failed = r.failed.length ? `; не вышло ${r.failed.length}: ${r.failed[0].error}` : "";
  return `${done}: ${vacancies(r.done.length)}${failed}`;
}

// Клавиши «Отсева» (#128): J/K — строка, X — выбрать текущую. По физической кнопке, вне полей ввода и диалогов
export function bindPickKeys(list: HTMLElement, onChange: () => void): void {
  const rows = () => [...list.querySelectorAll<PickElement>(".pick")];
  const current = () => list.querySelector<PickElement>(".pick.current");
  const select = (row: PickElement | undefined) => {
    if (!row) return;
    current()?.classList.remove("current");
    row.classList.add("current");
    row.focus({ preventScroll: true });
    row.scrollIntoView?.({ block: "nearest" });
  };
  list.addEventListener("focusin", (e) => {
    const row = (e.target as Element).closest?.<PickElement>(".pick");
    if (row && !row.classList.contains("current")) { current()?.classList.remove("current"); row.classList.add("current"); }
  });
  const onKey = (e: KeyboardEvent) => {
    if (!list.isConnected) { document.removeEventListener("keydown", onKey); return; }
    if (e.ctrlKey || e.metaKey || e.altKey || isTyping(e.target) || document.querySelector(".dialog-backdrop")) return;
    if (!["KeyJ", "KeyK", "KeyX"].includes(e.code)) return;
    e.preventDefault();
    const all = rows();
    const at = all.indexOf(current() as PickElement);
    if (e.code === "KeyJ") return select(all[Math.min(all.length - 1, at + 1)]);
    if (e.code === "KeyK") return select(all[Math.max(0, at < 0 ? 0 : at - 1)]);
    const row = current() ?? all[0];
    select(row);
    row.checked = !row.checked;
    onChange();
  };
  document.addEventListener("keydown", onKey);
}

// Группы — по убыванию числа, «Прочее» — последней
export const byCount = <T extends { id: string; count: number }>(groups: T[]): T[] =>
  [...groups].sort((a, b) => Number(a.id === "other") - Number(b.id === "other") || b.count - a.count);
