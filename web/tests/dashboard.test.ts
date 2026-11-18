import { describe, expect, it } from "vitest";
import type { Vacancy } from "../src/api/types";
import { DAY, dashboardData, delta, reached, reasonKey } from "../src/pages/dashboard-data";

const NOW = new Date("2026-11-18T12:00:00").getTime();
const ago = (days: number) => new Date(NOW - days * DAY).toISOString();

let id = 0;
function vacancy(extra: Partial<Vacancy>): Vacancy {
  return { id: String(++id), title: "C#", url: "https://hh.ru/vacancy/1", company: "Пример", score: 70, status: "New", ...extra };
}

describe("reached", () => {
  it("по истории, затем appliedAt, затем текущий статус", () => {
    expect(reached(vacancy({ history: [{ at: ago(3), to: "Queued" }] }), "Queued")).toBe(NOW - 3 * DAY);
    expect(reached(vacancy({ appliedAt: ago(2) }), "Applied")).toBe(NOW - 2 * DAY);
    expect(reached(vacancy({ status: "Invited", updatedAt: ago(1) }), "Invited")).toBe(NOW - DAY);
    expect(reached(vacancy({}), "Invited")).toBeNull();
  });
});

describe("delta", () => {
  it("к прошлому периоду той же длины", () => {
    expect(delta(12, 10, 30)).toEqual({ trend: "up", note: "+20% к прошлым 30 дн" });
    expect(delta(5, 10, 7)).toEqual({ trend: "down", note: "-50% к прошлым 7 дн" });
    expect(delta(3, 0, 30)).toEqual({ note: "новое за период" });
    expect(delta(0, 0, 30)).toEqual({ note: "—" });
  });
});

describe("reasonKey", () => {
  it("числа и слова в кавычках — общим местом", () => {
    expect(reasonKey("оценка 59 ниже 70: мало опыта")).toBe("оценка N ниже N");
    expect(reasonKey("стоп-слово «1С» в названии")).toBe("стоп-слово «…» в названии");
    expect(reasonKey(null)).toBe("без причины");
  });
});

describe("dashboardData", () => {
  const apps = [
    // Собрана 5 дней назад, отправлена, приглашение
    vacancy({ searchName: "dotnet", collectedAt: ago(5), llmScore: 80, history: [
      { at: ago(5), to: "Pending" }, { at: ago(4), to: "Queued" }, { at: ago(3), to: "Applied" }, { at: ago(1), to: "Invited" }] }),
    // Собрана 10 дней назад, отправлена, отказ
    vacancy({ searchName: "dotnet", collectedAt: ago(10), llmScore: 60, history: [{ at: ago(9), to: "Applied" }, { at: ago(8), to: "Rejected" }] }),
    // Отсеяна
    vacancy({ searchName: "backend", collectedAt: ago(2), status: "Skipped", statusReason: "оценка 40 ниже 70: стек" }),
    // Прошлый период (31–60 дней назад)
    vacancy({ searchName: "dotnet", collectedAt: ago(40), history: [{ at: ago(39), to: "Applied" }] }),
    // Другой агрегатор
    vacancy({ source: "hirify", collectedAt: ago(1) }),
  ];
  const d = dashboardData(apps, 30, "hh", NOW);

  it("плитки: собрано, отправлено, приглашения, отказы и прошлый период", () => {
    expect(d).toMatchObject({ cohort: 3, prevCohort: 1, applied: 2, prevApplied: 1, invited: 1, rejected: 1 });
  });

  it("воронка по собранным за период; отказ — отдельно", () => {
    expect(d.funnel.map(([label, value]) => [label, value])).toEqual([
      ["Собрано", 3], ["Оценено LLM", 2], ["В очередь", 2], ["Отправлено", 2], ["Приглашение", 1]]);
    expect(d.rejectedCohort).toBe(1);
  });

  it("по поискам — по убыванию собранного", () => {
    expect(d.bySearch).toEqual([
      { name: "dotnet", collected: 2, sent: 2, answered: 2, invited: 1 },
      { name: "backend", collected: 1, sent: 0, answered: 0, invited: 0 },
    ]);
  });

  it("причины отсева сгруппированы", () => {
    expect(d.topReasons).toEqual([["оценка N ниже N", 1]]);
  });

  it("по дням за 30 дней, по неделям за 90; отправленные и ответы — в своих столбцах", () => {
    expect(d.bucket).toBe(1);
    expect(d.series).toHaveLength(30);
    expect(d.series.reduce((s, p) => s + p.sent, 0)).toBe(2);
    expect(d.series.reduce((s, p) => s + p.answered, 0)).toBe(2);
    expect(d.series.at(-1)?.sent).toBe(0);
    const week = dashboardData(apps, 90, "hh", NOW);
    expect(week.bucket).toBe(7);
    expect(week.series).toHaveLength(13);
    expect(week.series.reduce((s, p) => s + p.sent, 0)).toBe(3);
  });

  it("«Все» — сумма источников", () => {
    expect(dashboardData(apps, 30, "all", NOW).cohort).toBe(4);
  });
});
