// Дашборд (#121): числа — с сервера (GET /api/dashboard, администратору — ещё /api/dashboard/system), раскладка — dashboard-data.ts
import { api } from "../api/http";
import type { CollectState, Dashboard, Dialog, Search, Stats as StatusStats, SystemDashboard, Telegram } from "../api/types";
import { ago, date } from "../format";
import { page, readOnly, scoped, session } from "../state";
import { AttentionCard, attentionItems } from "./attention";
import { Bars, Caption, Card, Chips, el, EmptyState, Funnel, Meter, Meta, nodes, Note, PageHead, pct, Pill, RankList, Segmented, Skeleton, StackTable, Stat, Stats,
  type ChoiceOption } from "../ui/ui";
import { delta, funnelRows, PERIODS, seriesBars } from "./dashboard-data";

export const AGGREGATORS: ChoiceOption<string>[] = [
  { value: "all", label: "Все" },
  { value: "hh", label: "hh.ru", color: "var(--src-hh)" },
  { value: "hirify", label: "hirify", color: "var(--src-hirify)", disabled: true, title: "После подключения (#32)" },
  { value: "getmatch", label: "getmatch", color: "var(--src-getmatch)", disabled: true, title: "После подключения (#32)" },
];
const state = { period: 30, source: "hh" };

const load = (period: number, source: string) => api<Dashboard>(scoped(`/api/dashboard?period=${period}&source=${source}`));

// Система — только администратору и только по своим данным: при просмотре чужого профиля блок не нужен
const showSystem = () => session.me?.role === "Admin" && !readOnly();

export async function dashboard(): Promise<HTMLElement> {
  const [first, system, searchList, chats, collect, stats, telegram] = await Promise.all([
    load(state.period, state.source),
    showSystem() ? api<SystemDashboard>(`/api/dashboard/system?period=${state.period}`).catch(() => null) : null,
    api<Search[]>(scoped("/api/searches")).catch(() => [] as Search[]),
    api<Dialog[]>(scoped("/api/dialogs?take=200")).catch(() => [] as Dialog[]),
    api<CollectState>("/api/collect/status").catch(() => null),
    api<StatusStats>(scoped("/api/stats")).catch(() => ({} as StatusStats)),
    readOnly() ? null : api<Telegram>("/api/telegram").catch(() => null),
  ]);
  const root = el("div");
  // «Требует внимания» (#128) — от периода и агрегатора не зависит: что ждёт решения сейчас
  const openDialogs = chats.filter((m) => !m.handledAt && m.kind !== "Rejection").length;
  const attention = AttentionCard(attentionItems(stats, telegram, readOnly(), openDialogs));
  const lastCollect = collect?.current ? "идёт сбор" : collect?.last?.finishedAt ? `сбор ${date(collect.last.finishedAt)}` : null;
  const renders = page.renders;
  let sys = system;

  // Смена периода или агрегатора — новый запрос; ответ, пришедший после следующей смены или ухода со страницы, не рисуется
  let asked = 0;
  async function reload() {
    const mine = ++asked;
    root.querySelector(".dash-body")?.replaceChildren(Skeleton({ lines: 6, block: true }));
    const [d, s] = await Promise.all([
      load(state.period, state.source),
      showSystem() ? api<SystemDashboard>(`/api/dashboard/system?period=${state.period}`).catch(() => null) : null,
    ]);
    if (mine !== asked || renders !== page.renders) return;
    sys = s;
    draw(d);
  }

  function draw(d: Dashboard) {
    const t = d.totals;
    const bars = seriesBars(d);
    const sourceName = AGGREGATORS.find((a) => a.value === d.source)?.label ?? d.source;
    const searchRows = d.bySearch.map((r) => [r.name, r.collected, r.sent, r.answered, r.invited, r.interviews,
      el("span", { style: r.invited ? "color:var(--ok)" : "", textContent: `${pct(r.invited, r.sent)}%` })]);
    const synced = d.chats.syncedAt
      ? `чаты синхронизированы ${ago(d.chats.syncedAt)}`
      : "чаты ещё не синхронизировались";

    root.replaceChildren(...nodes(
      PageHead("Дашборд", [synced, lastCollect, d.llm.limit ? `LLM сегодня ${d.llm.requests} из ${d.llm.limit}` : null].filter(Boolean).join(" · "),
        Segmented(PERIODS.map((p) => ({ value: p as number, label: `${p} дн` })), state.period, (p) => { state.period = p; void reload(); }, { label: "Период" })),
      Chips(AGGREGATORS, state.source, (s) => { state.source = s; void reload(); }, { label: "Агрегатор" }),
      attention,
      el("div", { className: "dash-body" },
        Stats([
          Stat({ value: t.collected, label: "собрано вакансий", ...delta(t.collected, t.prevCollected, d.period) }),
          Stat({ value: t.sent, label: "откликов отправлено", ...delta(t.sent, t.prevSent, d.period) }),
          Stat({ value: pct(t.invited, t.sent), unit: "%", label: "в приглашение", note: `${t.invited} приглашений · ${t.rejected} отказов` }),
          Stat({ value: t.interviews, label: "собеседований", ...delta(t.interviews, t.prevInterviews, d.period) }),
        ]),
        el("div", { className: "cols" },
          Card({}, Caption(`Воронка за ${d.period} дней · ${sourceName}`),
            Funnel(funnelRows(d), { max: d.funnel[0]?.count, note: "% — от предыдущего шага; приглашение, собеседование и отказ — от отправленных" })),
          Card({}, Caption(bars.week ? "Отклики по неделям" : "Отклики по дням"),
            Bars(bars.items, {
              from: bars.from, to: "сегодня",
              legend: [{ label: "отправлено", color: "var(--bar)" }, { label: "ответили отказом", color: "var(--accent)" },
                { label: "приглашение и собеседование", color: "var(--ok)" }],
            }))),
        el("div", { className: "cols wide-left" },
          Card({}, Caption(`По поискам · ${sourceName}`),
            searchRows.length
              ? StackTable([{ title: "Поиск" }, { title: "Собрано", num: true }, { title: "Отправлено", num: true }, { title: "Ответили", num: true },
                { title: "Приглашения", num: true }, { title: "Собеседования", num: true }, { title: "Конверсия", num: true }], searchRows)
              : EmptyState("За период ничего не собрано."),
            el("p", { className: "muted small", style: "margin:8px 0 0", textContent: `Поисков включено: ${searchList.filter((s) => s.enabled).length} из ${searchList.length}` })),
          Card({}, Caption("Почему отсеяно"),
            d.reasons.length ? RankList(d.reasons.map((r) => [r.reason, r.count])) : EmptyState("Отсеянных за период нет."))),
        Note("Ответы работодателей — на момент последней синхронизации чатов в расширении: время — по последнему новому сообщению."),
        sys ? SystemBlock(sys) : null)));
  }

  draw(first);
  return root;
}

// Блок «Система» (#121): сборка, сбор, LLM сервера, пользователи, уведомления
function SystemBlock(s: SystemDashboard): HTMLElement {
  const last = s.collection.last;
  const collect = s.collection.current
    ? `идёт: ${s.collection.current.search ?? ""}`.trim()
    : last ? `${date(last.finishedAt)} — новых ${last.added}, в очередь ${last.queued}${last.error ? `, ошибка: ${last.error}` : ""}` : "с запуска сервера не было";
  const req = s.llm.requests;
  const paused = s.llm.routes.filter((r) => r.connection.pausedUntil);
  const n = s.notifications;
  const users = s.users.map((u) => [
    el("span", {}, u.email, u.blocked ? Pill("заблокирован", { tone: "bad" }) : null),
    u.collected, u.sent, u.llmRequests, u.llmErrors,
    u.lastSentAt ? ago(u.lastSentAt) : "—", u.chatsSyncedAt ? ago(u.chatsSyncedAt) : "—"]);

  return el("section", { "aria-label": "Система" },
    el("h2", { textContent: "Система", style: "margin-top:var(--sp-4)" }),
    el("div", { className: "cols" },
      Card({}, Caption("Сервер"),
        Meta([`backend ${s.version.version}`, `коммит ${s.version.commit}`, `контракт API ${s.version.apiVersion}`, s.version.stage ?? null]),
        el("p", { className: "small", textContent: `Сбор: ${collect}` }),
        el("p", { className: "small", textContent: n.unsent
          ? `Неотправленных уведомлений: ${n.unsent}${n.failed ? `, со сбоем ${n.failed}` : ""}${n.oldestUnsentAt ? `, самое старое ${date(n.oldestUnsentAt)}` : ""}`
          : "Все уведомления отправлены" })),
      Card({}, Caption("LLM сервера за сутки"),
        el("p", { className: "small", textContent: `Запросов ${req.server}${req.limit ? ` из ${req.limit}` : ""}, ошибок за 24 ч ${s.llm.errors.total}` }),
        req.limit ? Meter(req.server, req.limit, { label: "Дневной лимит сервера", color: req.server >= req.limit ? "var(--bad)" : undefined }) : null,
        StackTable([{ title: "Задача" }, { title: "Модель" }, { title: "Вызовов", num: true }, { title: "Ошибок", num: true }],
          s.llm.routes.map((r) => [r.task, `${r.connection.name} · ${r.connection.model}`, r.calls, r.errors])),
        paused.length ? el("p", { className: "small", style: "color:var(--warn)", textContent: `На паузе: ${paused.map((r) => r.connection.name).join(", ")}` }) : null)),
    Card({ style: "margin-top:var(--sp-3)" }, Caption(`Пользователи за ${s.period} дней`),
      users.length
        ? StackTable([{ title: "Пользователь" }, { title: "Собрано", num: true }, { title: "Отправлено", num: true }, { title: "LLM", num: true },
          { title: "Ошибок LLM", num: true }, { title: "Последний отклик" }, { title: "Чаты" }], users)
        : EmptyState("Пользователей нет.")));
}
