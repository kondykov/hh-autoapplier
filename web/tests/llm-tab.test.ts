// «LLM → Отсев» (#127): фильтры в адресе, итог массового действия, строки с выбором и панель выбора kit
import { describe, expect, it, vi } from "vitest";
import { PickRow, SelectionBar } from "@hh-autoapplier/ui";
import { rejectsFilter, rejectsPath, rejectsQuery, summary, vacancies } from "../src/pages/llm-tab";

describe("фильтры «Отсева» в адресе", () => {
  it("по умолчанию — 30 дней, все группы, без пограничных", () => {
    const f = rejectsFilter(new URLSearchParams());
    expect(f).toEqual({ period: "30", group: null, border: false, n: 100 });
    expect(rejectsPath(f)).toBe("/llm/rejects");
    expect(rejectsQuery(f)).toBe("/api/llm/rejects?take=100&period=30");
  });

  it("период, группа, пограничные и «показать ещё» переживают перезагрузку", () => {
    const f = rejectsFilter(new URLSearchParams("period=all&group=stack&border=1&n=200"));
    expect(f).toEqual({ period: "all", group: "stack", border: true, n: 200 });
    expect(rejectsPath(f)).toBe("/llm/rejects?period=all&group=stack&border=1&n=200");
    expect(rejectsQuery(f)).toBe("/api/llm/rejects?take=200&group=stack&border=true");
    expect(rejectsPath(f, { group: null, n: 100 })).toBe("/llm/rejects?period=all&border=1");
  });

  it("мусор в адресе — значения по умолчанию", () => {
    expect(rejectsFilter(new URLSearchParams("period=90&n=-5"))).toMatchObject({ period: "30", n: 100 });
    expect(rejectsFilter(new URLSearchParams("n=100000")).n).toBe(500);
  });
});

describe("итог массового действия", () => {
  it("число и первая причина неудачи", () => {
    expect(vacancies(1)).toBe("1 вакансия");
    expect(vacancies(3)).toBe("3 вакансии");
    expect(vacancies(12)).toBe("12 вакансий");
    expect(summary("Вернули в очередь", { done: ["1", "2"], failed: [] })).toBe("Вернули в очередь: 2 вакансии");
    expect(summary("Скрыли", { done: ["1"], failed: [{ id: "2", error: "не найдена" }] })).toBe("Скрыли: 1 вакансия; не вышло 1: не найдена");
  });

  it("переоценка: в архиве и недоступные — отдельно (#147)", () => {
    expect(summary("Отправили на оценку заново", { done: ["1"], failed: [], archived: ["2", "3"], unavailable: ["4"] }))
      .toBe("Отправили на оценку заново: 1 вакансия; в архиве 2; недоступно 1");
    expect(summary("Отправили на оценку заново", { done: ["1"], failed: [], archived: [], unavailable: [] }))
      .toBe("Отправили на оценку заново: 1 вакансия");
  });
});

describe("PickRow и SelectionBar", () => {
  it("выбор строк, «выбрать все» и кнопки только при выборе", () => {
    const toggled = vi.fn();
    const rows = [PickRow({ title: "A", onToggle: toggled }), PickRow({ title: "B", onToggle: toggled, checked: true })];
    expect(rows[1].classList.contains("on")).toBe(true);
    const bar = SelectionBar(() => [document.createElement("button")], { onAll: (on) => rows.forEach((r) => { r.checked = on; }) });
    document.body.append(bar);
    bar.update(1, 2);
    const all = bar.querySelector<HTMLInputElement>("input")!;
    expect(all.indeterminate).toBe(true);
    expect(bar.textContent).toContain("1 выбрано");

    all.checked = true;
    all.dispatchEvent(new Event("change"));
    expect(rows.every((r) => r.checked && r.classList.contains("on"))).toBe(true);

    bar.update(0, 2);
    expect([...bar.querySelectorAll<HTMLButtonElement>(".selbar-acts button")].every((b) => b.disabled)).toBe(true);

    const box = rows[0].querySelector("input")!;
    box.checked = true;
    box.dispatchEvent(new Event("change"));
    expect(toggled).toHaveBeenCalledWith(true);
    bar.dispose();
    expect(bar.isConnected).toBe(false);
  });
});
