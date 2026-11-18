// Дашборд (#121): разметка; числа — dashboard-data.ts
import { api } from "../api/http";
import type { CollectState, Dialog, Search, Stats as StatusStats, Telegram, Vacancy } from "../api/types";
import { date } from "../format";
import { readOnly, scoped } from "../state";
import { AttentionCard, attentionItems } from "./attention";
import { Bars, Caption, Card, Chips, el, EmptyState, Funnel, nodes, Note, PageHead, pct, RankList, Segmented, StackTable, Stat, Stats, type ChoiceOption } from "../ui/ui";
import { dashboardData, delta, PERIODS } from "./dashboard-data";

export const AGGREGATORS: ChoiceOption<string>[] = [
  { value: "all", label: "Все" },
  { value: "hh", label: "hh.ru", color: "var(--src-hh)" },
  { value: "hirify", label: "hirify", color: "var(--src-hirify)", disabled: true, title: "После подключения (#32)" },
  { value: "getmatch", label: "getmatch", color: "var(--src-getmatch)", disabled: true, title: "После подключения (#32)" },
];
const state = { period: 30, source: "hh" };

async function allApplications(): Promise<Vacancy[]> {
  const items: Vacancy[] = [];
  for (let skip = 0; ; skip += 500) {
    const page = await api<Vacancy[]>(scoped(`/api/vacancies?skip=${skip}&take=500&includeHidden=true`));
    items.push(...page);
    if (page.length < 500 || skip > 20000) return items;
  }
}

export async function dashboard(): Promise<HTMLElement> {
  const [apps, searchList, chats, collect, stats, telegram] = await Promise.all([
    allApplications(),
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

  const lastChat = chats.reduce((max, m) => Math.max(max, new Date(m.at).getTime() || 0), 0);
  const lastCollect = collect?.current ? "идёт сбор" : collect?.last?.finishedAt ? `сбор ${date(collect.last.finishedAt)}` : null;

  function draw() {
    const d = dashboardData(apps, state.period, state.source);
    const f = d.funnel;
    const sent = f[3][1];

    const dayLabel = (t: number) => new Date(t).toLocaleDateString("ru-RU", { day: "numeric", month: "short" });
    // Столбец — больше из «отправлено» и «ответили»; внутри — приглашения и отказы
    const bars = Bars(d.series.map((s) => ({
      total: Math.max(s.sent, s.answered),
      title: `${dayLabel(s.at)}${d.bucket > 1 ? " (неделя)" : ""}: отправлено ${s.sent}, ответов ${s.answered}, приглашений ${s.invited}`,
      parts: [{ value: s.answered ? s.invited : 0, tone: "ok" as const }, { value: s.answered ? s.answered - s.invited : 0, tone: "accent" as const }],
    })), {
      from: dayLabel(d.first), to: "сегодня",
      legend: [{ label: "отправлено", color: "var(--bar)" }, { label: "ответили отказом", color: "var(--accent)" }, { label: "приглашение", color: "var(--ok)" }],
    });

    const searchRows = d.bySearch.map((r) => [r.name, r.collected, r.sent, r.answered, r.invited,
      el("span", { style: r.invited ? "color:var(--ok)" : "", textContent: `${pct(r.invited, r.sent)}%` })]);
    const sourceName = AGGREGATORS.find((a) => a.value === state.source)?.label ?? state.source;

    root.replaceChildren(...nodes(
      PageHead("Дашборд", [
        lastChat ? `последнее сообщение из чатов ${date(lastChat)}` : "чаты ещё не синхронизировались",
        lastCollect,
      ].filter(Boolean).join(" · "),
      Segmented(PERIODS.map((p) => ({ value: p as number, label: `${p} дн` })), state.period, (p) => { state.period = p; draw(); }, { label: "Период" })),
      Chips(AGGREGATORS, state.source, (s) => { state.source = s; draw(); }, { label: "Агрегатор" }),
      attention,
      Note("Демо #121: числа считаются в браузере по истории откликов. Ответы работодателей — на момент последней синхронизации чатов в расширении."),
      Stats([
        Stat({ value: d.cohort, label: "собрано вакансий", ...delta(d.cohort, d.prevCohort, state.period) }),
        Stat({ value: d.applied, label: "откликов отправлено", ...delta(d.applied, d.prevApplied, state.period) }),
        Stat({ value: pct(d.invited, d.applied), unit: "%", label: "в приглашение", note: `${d.invited} приглашений за период` }),
        Stat({ value: d.rejected, label: "отказов", note: "собеседования — после #122" }),
      ]),
      el("div", { className: "cols" },
        Card({}, Caption(`Воронка за ${state.period} дней · ${sourceName}`),
          Funnel([
            ...f.map(([label, value, color], i) => ({ label, value, base: i ? f[i - 1][1] : null, color })),
            { label: "Отказ", value: d.rejectedCohort, base: sent, color: "var(--bad)" },
          ], { max: d.cohort, note: "% — от предыдущего шага; отказ — от отправленных" })),
        Card({}, Caption(d.bucket > 1 ? "Отклики по неделям" : "Отклики по дням"), bars)),
      el("div", { className: "cols wide-left" },
        Card({}, Caption(`По поискам · ${sourceName}`),
          searchRows.length ? StackTable([{ title: "Поиск" }, { title: "Собрано", num: true }, { title: "Отправлено", num: true }, { title: "Ответили", num: true }, { title: "Приглашения", num: true }, { title: "Конверсия", num: true }], searchRows)
            : EmptyState("За период ничего не собрано."),
          el("p", { className: "muted small", style: "margin:8px 0 0", textContent: `Поисков включено: ${searchList.filter((s) => s.enabled).length} из ${searchList.length}` })),
        Card({}, Caption("Почему отсеяно"),
          d.topReasons.length ? RankList(d.topReasons) : EmptyState("Отсеянных за период нет.")))));
  }

  draw();
  return root;
}
