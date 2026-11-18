// Расчёты дашборда (#121), демо: считается в браузере по тем же данным, что «Вакансии» (история статусов каждого отклика).
// В задаче — эндпоинт GET /api/dashboard: тогда страница перестанет выкачивать все отклики. Без DOM — проверяется тестами
import type { Vacancy } from "../api/types";

export const DAY = 86400000;
export const PERIODS = [7, 30, 90] as const;

// Когда отклик впервые пришёл в статус (по истории); null — не приходил
export function reached(v: Vacancy, status: string): number | null {
  const h = (v.history ?? []).find((x) => x.to === status);
  if (h) return new Date(h.at).getTime();
  if (status === "Applied" && v.appliedAt) return new Date(v.appliedAt).getTime();
  return v.status === status && v.updatedAt ? new Date(v.updatedAt).getTime() : null;
}
export const collected = (v: Vacancy): number => new Date(v.collectedAt ?? v.history?.[0]?.at ?? 0).getTime();

// Дельта к прошлому периоду той же длины
export function delta(a: number, b: number, period: number): { trend?: "up" | "down" | null; note: string } {
  if (!b) return { note: a ? "новое за период" : "—" };
  const d = Math.round(((a - b) / b) * 100);
  return { trend: d > 0 ? "up" : d < 0 ? "down" : null, note: `${d > 0 ? "+" : ""}${d}% к прошлым ${period} дн` };
}

// Причина отсева — по первой части («правило: …», «LLM: …»); числа и слова в кавычках — общим местом,
// чтобы «оценка 59 ниже 70» и «оценка 62 ниже 70» легли в одну строку
export const reasonKey = (reason: string | null | undefined): string =>
  (reason ?? "без причины").split(/[:.(]/)[0].replace(/«[^»]*»/g, "«…»").replace(/\d+/g, "N").trim().slice(0, 70) || "без причины";

export interface SearchRow {
  name: string;
  collected: number;
  sent: number;
  answered: number;
  invited: number;
}

export interface SeriesPoint {
  at: number;
  sent: number;
  answered: number;
  invited: number;
}

export interface DashboardData {
  cohort: number;
  prevCohort: number;
  applied: number;
  prevApplied: number;
  invited: number;
  rejected: number;
  // Воронка по вакансиям, собранным за период: [подпись, число, цвет]
  funnel: [string, number, string?][];
  rejectedCohort: number;
  // Отклики по дням (за 90 дней — по неделям): bucket — дней в столбце, first — начало первого столбца
  bucket: number;
  first: number;
  series: SeriesPoint[];
  bySearch: SearchRow[];
  topReasons: [string, number][];
}

// apps — все отклики пользователя; source — агрегатор или "all"; now — сейчас (для тестов — своё)
export function dashboardData(apps: Vacancy[], period: number, source: string, now = Date.now()): DashboardData {
  const from = now - period * DAY;
  const prevFrom = from - period * DAY;
  // Источник: пока у всех откликов — hh.ru; «Все» = сумма источников
  const pool = apps.filter((v) => source === "all" || (v.source ?? "hh") === source);
  const inPeriod = (t: number | null, a = from, b = now) => t != null && t >= a && t < b;

  const cohort = pool.filter((v) => inPeriod(collected(v)));
  const prevCohort = pool.filter((v) => inPeriod(collected(v), prevFrom, from));
  const appliedIn = (a: number, b: number) => pool.filter((v) => inPeriod(reached(v, "Applied"), a, b));

  // Воронка: % — от предыдущего шага, отказ — от отправленных
  const step = (pred: (v: Vacancy) => boolean) => cohort.filter(pred).length;
  const funnel: [string, number, string?][] = [
    ["Собрано", cohort.length],
    ["Оценено LLM", step((v) => v.llmScore != null)],
    ["В очередь", step((v) => reached(v, "Queued") != null || reached(v, "Applied") != null)],
    ["Отправлено", step((v) => reached(v, "Applied") != null)],
    ["Приглашение", step((v) => reached(v, "Invited") != null), "var(--ok)"],
  ];

  // Отклики по дням: отправлено, ответы (приглашение или отказ), из них приглашения
  const bucket = period > 30 ? 7 : 1;
  const buckets = Math.ceil(period / bucket);
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);
  const first = start.getTime() - (buckets * bucket - 1) * DAY;
  const series: SeriesPoint[] = Array.from({ length: buckets }, (_, i) => ({ at: first + i * bucket * DAY, sent: 0, answered: 0, invited: 0 }));
  const add = (t: number | null, key: "sent" | "answered" | "invited") => {
    if (t == null || t < first) return;
    const i = Math.floor((t - first) / (bucket * DAY));
    if (series[i]) series[i][key]++;
  };
  for (const v of pool) {
    add(reached(v, "Applied"), "sent");
    add(reached(v, "Invited"), "invited");
    add(reached(v, "Invited"), "answered");
    add(reached(v, "Rejected"), "answered");
  }

  // По поискам: собрано за период, отправлено, ответили, приглашения
  const bySearch = new Map<string, SearchRow>();
  for (const v of cohort) {
    const name = v.searchName ?? "без поиска";
    const row = bySearch.get(name) ?? { name, collected: 0, sent: 0, answered: 0, invited: 0 };
    row.collected++;
    if (reached(v, "Applied") != null) row.sent++;
    if (reached(v, "Invited") != null) row.invited++;
    if (reached(v, "Invited") != null || reached(v, "Rejected") != null) row.answered++;
    bySearch.set(name, row);
  }

  const reasons = new Map<string, number>();
  for (const v of cohort.filter((x) => x.status === "Skipped")) {
    const key = reasonKey(v.statusReason);
    reasons.set(key, (reasons.get(key) ?? 0) + 1);
  }

  return {
    cohort: cohort.length,
    prevCohort: prevCohort.length,
    applied: appliedIn(from, now).length,
    prevApplied: appliedIn(prevFrom, from).length,
    invited: pool.filter((v) => inPeriod(reached(v, "Invited"))).length,
    rejected: pool.filter((v) => inPeriod(reached(v, "Rejected"))).length,
    funnel,
    rejectedCohort: step((v) => reached(v, "Rejected") != null),
    bucket,
    first,
    series,
    bySearch: [...bySearch.values()].sort((a, b) => b.collected - a.collected),
    topReasons: [...reasons.entries()].sort((a, b) => b[1] - a[1]).slice(0, 6),
  };
}
