// Дашборд (#121): числа считает сервер (GET /api/dashboard), здесь — подписи и раскладка для kit. Без DOM — проверяется тестами
import type { Dashboard } from "../api/types";

export const PERIODS = [7, 30, 90] as const;

// Дельта к прошлому периоду той же длины
export function delta(a: number, b: number, period: number): { trend?: "up" | "down" | null; note: string } {
  if (!b) return { note: a ? "новое за период" : "—" };
  const d = Math.round(((a - b) / b) * 100);
  return { trend: d > 0 ? "up" : d < 0 ? "down" : null, note: `${d > 0 ? "+" : ""}${d}% к прошлым ${period} дн` };
}

const STEP_COLORS: Record<string, string> = { invited: "var(--ok)", interview: "var(--ok)", rejected: "var(--bad)" };

// Шаги воронки для Funnel: процент — от шага base (у отказа — от отправленных)
export function funnelRows(d: Dashboard): { label: string; value: number; base: number | null; color?: string }[] {
  const count = new Map(d.funnel.map((s) => [s.key, s.count]));
  return d.funnel.map((s) => ({ label: s.label, value: s.count, base: s.base == null ? null : count.get(s.base) ?? 0, color: STEP_COLORS[s.key] }));
}

// Дни на сервере — по Москве: подписи тоже
export const dayLabel = (t: string | number) =>
  new Date(t).toLocaleDateString("ru-RU", { day: "numeric", month: "short", timeZone: "Europe/Moscow" });

// Столбцы откликов: высота — больше из «отправлено» и «ответили»; внутри — приглашения и собеседования (ok) и отказы (accent)
export function seriesBars(d: Dashboard) {
  const week = d.series.bucket > 1;
  return {
    items: d.series.points.map((p) => ({
      total: Math.max(p.sent, p.answered),
      title: `${dayLabel(p.at)}${week ? " (неделя)" : ""}: отправлено ${p.sent}, ответов ${p.answered}, приглашений ${p.invited}, собеседований ${p.interviews}`,
      parts: [{ value: p.invited, tone: "ok" as const }, { value: Math.max(0, p.answered - p.invited), tone: "accent" as const }],
    })),
    from: d.series.points.length ? dayLabel(d.series.points[0].at) : "",
    week,
  };
}
