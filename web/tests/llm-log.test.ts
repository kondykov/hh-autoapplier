// «LLM → Журнал» и «Подключения» (#127): фильтры журнала в адресе, свёрнутые повторы, итог строки, что проверяет «Проверить»
import { describe, expect, it } from "vitest";
import type { LlmCallEntry } from "../src/api/types";
import { checkPath } from "../src/pages/llm-connections";
import { entryTime, journalFilter, journalPath, journalQuery, outcome } from "../src/pages/llm-log";

const entry = (patch: Partial<LlmCallEntry> = {}): LlmCallEntry => ({
  id: 7, at: "2026-11-19T10:41:00Z", since: "2026-11-19T10:41:00Z", count: 1, purposes: ["vacancy-evaluation"], model: "gpt-oss", connection: null,
  outcome: "ok", detail: "подходит 82", errorKind: null, promptTokens: 100, completionTokens: 20, user: null, vacancyId: "1", vacancy: "Разработчик .NET",
  ...patch,
});

describe("фильтры журнала в адресе", () => {
  it("по умолчанию — все вызовы, 100 строк", () => {
    const f = journalFilter(new URLSearchParams());
    expect(f).toEqual({ purpose: "", model: "", errors: false, n: 100, call: null });
    expect(journalPath(f)).toBe("/llm/journal");
    expect(journalQuery(f)).toBe("/api/llm/calls?take=100");
  });

  it("задача, модель, только ошибки и открытый вызов переживают перезагрузку", () => {
    const f = journalFilter(new URLSearchParams("purpose=cover-letter&model=m&errors=1&n=200&call=42"));
    expect(f).toEqual({ purpose: "cover-letter", model: "m", errors: true, n: 200, call: 42 });
    expect(journalPath(f)).toBe("/llm/journal?purpose=cover-letter&model=m&errors=1&n=200&call=42");
    expect(journalQuery(f)).toBe("/api/llm/calls?take=200&purpose=cover-letter&model=m&errors=true");
    expect(journalPath(f, { call: null, errors: false })).toBe("/llm/journal?purpose=cover-letter&model=m&n=200");
  });

  it("мусор в адресе — значения по умолчанию", () => {
    expect(journalFilter(new URLSearchParams("call=abc&n=-1"))).toMatchObject({ call: null, n: 100 });
  });
});

describe("строка журнала", () => {
  it("свёрнутые повторы — диапазон времени и ×N", () => {
    const e = entry({ count: 1968, since: "2026-11-19T05:02:00Z", outcome: "error", model: "", errorKind: "limit", detail: "исчерпан дневной лимит (300)" });
    expect(entryTime(e)).toMatch(/ – \d\d:\d\d$/);
    expect(outcome(e).textContent).toBe("лимит ×1968 исчерпан дневной лимит (300)");
  });

  it("удачная оценка — модель и итог; отклонённый ответ — отдельной пометкой", () => {
    expect(outcome(entry()).textContent).toBe("gpt-oss подходит 82");
    expect(outcome(entry({ outcome: "rejected", detail: "цифры не из фактов: 15" })).textContent).toContain("отклонено проверкой");
  });
});

describe("«Проверить» в «Подключениях»", () => {
  it("подключение — по имени, маршрут — по задаче, основной — без параметров", () => {
    expect(checkPath("named", "groq fast")).toBe("/api/llm/check?connection=groq%20fast");
    expect(checkPath("route", "Letters")).toBe("/api/llm/check?task=Letters");
    expect(checkPath("main", "main")).toBe("/api/llm/check");
  });
});
