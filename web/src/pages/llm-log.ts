// LLM (#127): «Журнал» — вызовы LLM, повторы одной ошибки подряд свёрнуты, промпт и ответ — по клику (только текстом);
// «Письма» — письма LLM, отклонённые проверкой, с «Написать заново». Фильтры журнала и открытый вызов — в адресе
import { api, post } from "../api/http";
import type { LlmCall, LlmCallEntry, LlmCalls, LlmLetter } from "../api/types";
import { date } from "../format";
import { path, type Route } from "../router";
import { act, app, readOnly, scoped, session } from "../state";
import { Badge, Button, Caption, Card, Chips, el, EmptyState, Meta, Note, Pill, Pre, Quote, StackTable, Stat, Stats, Toolbar, type Child, type Tone } from "@hh-autoapplier/ui";

const PAGE = 100;

// Назначения вызовов (LlmCall.Purpose) — подписи как задачи
export const PURPOSES: Record<string, string> = {
  "vacancy-evaluation": "Оценка", "letter-requests": "Просьбы к письму", "cover-letter": "Письмо", questionnaire: "Анкета",
  "dialog-analysis": "Ответ работодателя", check: "Проверка связи",
};
const purposeTitle = (p: string) => PURPOSES[p] ?? p;

const ERROR_KINDS: Record<string, string> = { limit: "лимит", network: "сеть", provider: "провайдер", other: "ошибка" };

// ── Журнал ────────────────────────────────────────────────────────────
export interface JournalFilter {
  purpose: string;
  model: string;
  errors: boolean;
  n: number;
  call: number | null;
}

// Фильтры — из адреса: purpose, model, errors=1, n — сколько строк показано, call — открытый вызов
export function journalFilter(q: URLSearchParams): JournalFilter {
  const call = Number(q.get("call"));
  return {
    purpose: q.get("purpose") ?? "", model: q.get("model") ?? "", errors: q.get("errors") === "1",
    n: Math.min(500, Math.max(PAGE, Number(q.get("n")) || PAGE)), call: Number.isInteger(call) && call > 0 ? call : null,
  };
}

export function journalPath(f: JournalFilter, patch: Partial<JournalFilter> = {}): string {
  const x = { ...f, ...patch };
  return path("llm", { purpose: x.purpose, model: x.model, errors: x.errors ? "1" : null, n: x.n > PAGE ? String(x.n) : null, call: x.call ? String(x.call) : null },
    "journal");
}

export function journalQuery(f: JournalFilter): string {
  const q = new URLSearchParams({ take: String(f.n) });
  if (f.purpose) q.set("purpose", f.purpose);
  if (f.model) q.set("model", f.model);
  if (f.errors) q.set("errors", "true");
  return `/api/llm/calls?${q}`;
}

const time = (iso: string) => new Date(iso).toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });

// Время строки: у свёрнутых повторов — диапазон «19.11.2026, 08:02 – 10:41»
export function entryTime(e: Pick<LlmCallEntry, "at" | "since" | "count">): string {
  if (e.count < 2) return date(e.at);
  const sameDay = new Date(e.since).toDateString() === new Date(e.at).toDateString();
  return `${date(e.since)} – ${sameDay ? time(e.at) : date(e.at)}`;
}

// Итог одной строкой: модель и что вышло; у ошибок — вид и сколько раз подряд
export function outcome(e: LlmCallEntry): HTMLElement {
  const model = e.model ? Pill(e.model, { dot: e.outcome === "ok" ? "var(--ok)" : e.outcome === "rejected" ? "var(--warn)" : "var(--bad)" }) : null;
  const via = e.connection ? el("span", { className: "muted small", textContent: ` через ${e.connection}` }) : null;
  const lead = model ? [model, via, " "] : null;
  if (e.outcome === "ok") return el("span", {}, lead, e.detail ? el("span", { textContent: e.detail }) : el("span", { className: "muted", textContent: "ответ принят" }));
  const tone: Tone = "bad";
  const badge = e.outcome === "rejected" ? Badge("отклонено проверкой", { tone: "accent" }) : Badge(ERROR_KINDS[e.errorKind ?? "other"] ?? "ошибка", { tone });
  return el("span", {}, lead, badge, e.count > 1 ? el("b", { textContent: ` ×${e.count} ` }) : " ", el("span", { className: "muted", textContent: e.detail ?? "" }));
}

export async function journal(route: Route): Promise<Child> {
  const f = journalFilter(route.query);
  const [data, call] = await Promise.all([
    api<LlmCalls>(scoped(journalQuery(f))),
    f.call ? api<LlmCall>(`/api/llm/calls/${f.call}`).catch((e: Error) => e) : Promise.resolve(null),
  ]);
  const admin = session.me?.role === "Admin" && !readOnly();
  const go = (patch: Partial<JournalFilter>) => app.navigate(journalPath(f, { ...patch, n: PAGE, call: null }));

  const filters = Toolbar([
    Chips([{ value: false, label: "все" }, { value: true, label: `только ошибки · ${f.errors ? data.total : data.errors}`, color: "var(--bad)" }], f.errors,
      (errors) => go({ errors }), { label: "Итог" }),
    Chips([{ value: "", label: "все задачи" }, ...data.purposes.map((p) => ({ value: p.id, label: `${purposeTitle(p.id)} · ${p.count}` }))], f.purpose,
      (purpose) => go({ purpose }), { label: "Задача" }),
    data.models.length > 1 || f.model
      ? Chips([{ value: "", label: "все модели" }, ...data.models.map((m) => ({ value: m.id, label: `${m.id} · ${m.count}` }))], f.model, (model) => go({ model }),
        { label: "Модель" })
      : null,
  ]);

  const collapsed = data.items.filter((i) => i.count > 1).reduce((s, i) => s + i.count - 1, 0);
  const tiles = Stats([
    Stat({ value: data.total, label: "вызовов по фильтру", note: `строк ${data.matched}${collapsed ? ` · свёрнуто повторов ${collapsed} (на экране)` : ""}` }),
    Stat({ value: data.errors, label: "с ошибкой", note: data.total ? `${Math.round((data.errors / data.total) * 100)}% вызовов` : "вызовов нет", trend: data.errors ? "down" : null }),
  ]);

  if (!data.items.length)
    return [filters, Card({}, EmptyState(f.errors || f.purpose || f.model ? "По фильтру вызовов нет." : "Вызовов LLM пока нет.", { icon: "llm" }))];

  const columns = [{ title: "Время" }, { title: "Задача" }, { title: "Модель и итог" }, { title: "Токены", num: true }, ...(admin ? [{ title: "Пользователь" }] : []), { title: "" }];
  const rows = data.items.map((e) => [
    entryTime(e),
    el("span", {}, e.purposes.map(purposeTitle).join(", "), e.vacancy ? el("div", { className: "muted small", textContent: e.vacancy }) : null),
    outcome(e),
    e.promptTokens || e.completionTokens ? `${e.promptTokens} + ${e.completionTokens}` : "—",
    ...(admin ? [e.user ?? "—"] : []),
    el("a", { href: journalPath(f, { call: e.id }), className: e.id === f.call ? "active" : "", textContent: e.count > 1 ? "Последний" : "Открыть",
      "aria-label": `Промпт и ответ вызова ${e.id}` }),
  ]);
  const more = data.matched > data.items.length && f.n < 500
    ? el("p", {}, el("a", { href: journalPath(f, { n: f.n + PAGE }), textContent: `Показать ещё (${data.items.length} из ${data.matched})` }))
    : el("p", { className: "muted small", textContent: `Показано ${data.items.length} из ${data.matched} строк` });

  // Открытый вызов — над списком на всю ширину: промпты длинные, в колонке рядом с таблицей им тесно
  return [filters, tiles, call ? callCard(call, f) : null, Card({}, StackTable(columns, rows), more)];
}

// Промпт и ответ вызова — только текстом (Pre), без разметки
function callCard(call: LlmCall | Error, f: JournalFilter): HTMLElement {
  if (call instanceof Error) return Card({ title: "Промпт и ответ" }, EmptyState(`Вызов недоступен: ${call.message}.`));
  return Card({ title: `Вызов ${call.id}`, actions: [el("a", { href: journalPath(f, { call: null }), textContent: "Закрыть" })] },
    Meta([date(call.at), purposeTitle(call.purpose), call.model || "без модели", call.connection ? `через ${call.connection}` : null,
      call.promptTokens || call.completionTokens ? `токены ${call.promptTokens} + ${call.completionTokens}` : null]),
    call.error ? Note(`Ошибка: ${call.error}`) : null,
    Caption("Промпт"), Pre(call.prompt, { scroll: true }),
    Caption("Ответ"), call.response != null ? Pre(call.response, { scroll: true }) : el("p", { className: "muted", textContent: "Ответа не было." }));
}

// ── Письма ────────────────────────────────────────────────────────────
export async function letters(): Promise<Child> {
  const items = await api<LlmLetter[]>(scoped("/api/llm/letters"));
  const editable = !readOnly();
  if (!items.length) return Card({}, EmptyState("Писем, отклонённых проверкой, нет.", { icon: "check" }));

  const rewrite = (l: LlmLetter) => Button(l.rewritten ? "Переписать ещё раз" : "Написать заново", { icon: "llm", onClick: async () => {
    const r = await act(() => post<{ written: number; failed: number; stoppedBy: string | null }>(`/api/llm/letters/${encodeURIComponent(l.vacancyId)}/rewrite`),
      (r) => r.written ? `«${l.title}»: письмо переписано — одобрите его в «Проверке»` : `«${l.title}»: ${r.stoppedBy ?? "письмо снова не прошло проверку"}`);
    if (r) app.render();
  } });

  const cards = items.map((l) => Card({
    title: l.title, href: l.url ?? undefined,
    meta: [l.company, l.model ? `модель ${l.model}` : null, l.at ? date(l.at) : null],
    actions: [
      el("a", { href: path("review"), textContent: "Открыть в «Проверке»" }),
      el("a", { href: path("vacancies", { view: "all", search: l.title }), textContent: "В «Вакансиях»" }),
      editable ? rewrite(l) : null,
    ],
  },
  el("p", {}, l.rewritten ? Badge("переписано — ждёт одобрения", { tone: "ok" }) : Badge("отклонено проверкой", { tone: "bad" }), " ", el("span", { textContent: l.reason })),
  l.letter ? Quote(l.letter) : el("p", { className: "muted", textContent: "Черновика в журнале нет." })));

  return [
    el("p", { className: "muted", textContent: `Писем на проверке из-за проверки фактов: ${items.length}. Письмо без проверки не уходит — одобрить как есть или поправить можно в «Проверке»; «Написать заново» — сразу, запрос дневного лимита.` }),
    ...cards,
  ];
}
