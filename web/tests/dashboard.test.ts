import { describe, expect, it } from "vitest";
import type { Dashboard } from "../src/api/types";
import { ago } from "../src/format";
import { delta, funnelRows, seriesBars } from "../src/pages/dashboard-data";

const NOW = new Date("2026-11-18T12:00:00Z").getTime();

// Ответ GET /api/dashboard (#121): числа считает сервер
const dashboard: Dashboard = {
  period: 7,
  source: "hh",
  from: "2026-11-11T12:00:00Z",
  to: "2026-11-18T12:00:00Z",
  totals: { collected: 10, prevCollected: 8, sent: 4, prevSent: 2, invited: 2, prevInvited: 0, interviews: 1, prevInterviews: 0, rejected: 1, prevRejected: 1 },
  funnel: [
    { key: "collected", label: "Собрано", count: 10, base: null },
    { key: "llm", label: "Оценено LLM", count: 8, base: "collected" },
    { key: "queued", label: "В очередь", count: 5, base: "llm" },
    { key: "sent", label: "Отправлено", count: 4, base: "queued" },
    { key: "invited", label: "Приглашение", count: 2, base: "sent" },
    { key: "interview", label: "Собеседование", count: 1, base: "invited" },
    { key: "rejected", label: "Отказ", count: 1, base: "sent" },
  ],
  series: { bucket: 1, points: [
    { at: "2026-11-16T00:00:00+03:00", sent: 3, answered: 1, invited: 1, interviews: 0 },
    { at: "2026-11-17T00:00:00+03:00", sent: 0, answered: 2, invited: 1, interviews: 1 },
  ] },
  bySearch: [],
  reasons: [],
  sources: [{ source: "hh", collected: 10, sent: 4, invited: 2, interviews: 1 }],
  llm: { requests: 3, errors: 0, limit: 150 },
  chats: { syncedAt: null, messages: 0 },
};

describe("delta", () => {
  it("к прошлому периоду той же длины", () => {
    expect(delta(12, 10, 30)).toEqual({ trend: "up", note: "+20% к прошлым 30 дн" });
    expect(delta(5, 10, 7)).toEqual({ trend: "down", note: "-50% к прошлым 7 дн" });
    expect(delta(3, 0, 30)).toEqual({ note: "новое за период" });
    expect(delta(0, 0, 30)).toEqual({ note: "—" });
    expect(delta(48, 1, 30)).toEqual({ trend: "up", note: "+47 к прошлым 30 дн" });
  });
});

describe("funnelRows", () => {
  it("процент — от шага base; отказ — от отправленных, собеседование — от приглашений", () => {
    const rows = funnelRows(dashboard);
    expect(rows.map((r) => [r.label, r.value, r.base])).toEqual([
      ["Собрано", 10, null], ["Оценено LLM", 8, 10], ["В очередь", 5, 8], ["Отправлено", 4, 5],
      ["Приглашение", 2, 4], ["Собеседование", 1, 2], ["Отказ", 1, 4]]);
    expect(rows.at(-1)?.color).toBe("var(--bad)");
  });
});

describe("seriesBars", () => {
  it("высота — больше из отправленных и ответов; приглашения и отказы — частями", () => {
    const bars = seriesBars(dashboard);
    expect(bars.week).toBe(false);
    expect(bars.items.map((i) => i.total)).toEqual([3, 2]);
    expect(bars.items[1].parts).toEqual([{ value: 1, tone: "ok" }, { value: 1, tone: "accent" }]);
    expect(bars.items[1].title).toContain("собеседований 1");
    expect(bars.from).toBe("16 нояб.");
  });
});

describe("ago", () => {
  it("минуты, часы, дни", () => {
    expect(ago(NOW - 20_000, NOW)).toBe("только что");
    expect(ago(NOW - 5 * 60_000, NOW)).toBe("5 мин назад");
    expect(ago(NOW - 3 * 3_600_000, NOW)).toBe("3 ч назад");
    expect(ago(NOW - 50 * 3_600_000, NOW)).toBe("2 дн назад");
  });
});
