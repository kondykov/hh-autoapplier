// «Мои модели» (#107): варианты выбора и состояние подключения
import { describe, expect, it } from "vitest";
import type { LlmConnection } from "../src/api/types";
import { choiceOptions, connectionState } from "../src/pages/llm";
import { parseRoute } from "../src/router";

const conn = (over: Partial<LlmConnection> = {}): LlmConnection =>
  ({ name: "gigachat", provider: "gigachat", model: "GigaChat-2-Max", available: true, usedToday: 188, dailyLimit: 1000, ...over });

describe("connectionState", () => {
  it("остаток, без лимита, исчерпан, пауза", () => {
    expect(connectionState(conn())).toBe("осталось 812");
    expect(connectionState(conn({ dailyLimit: 0 }))).toBe("без лимита");
    expect(connectionState(conn({ available: false, usedToday: 1000 }))).toBe("лимит исчерпан");
    expect(connectionState(conn({ available: false, pausedUntil: "2026-11-19T12:20:00Z" }))).toMatch(/^пауза до \d\d:\d\d$/);
  });
});

describe("choiceOptions", () => {
  const server = conn({ name: "main", model: "openai/gpt-oss-120b" });

  it("сначала «как на сервере» с моделью сервера, затем подключения с пометкой недоступных", () => {
    const options = choiceOptions([conn(), conn({ name: "groq-20b", model: "gpt-oss-20b", available: false, pausedUntil: "2026-11-19T12:20:00Z" })], server, null);
    expect(options.map(([v]) => v)).toEqual(["", "gigachat", "groq-20b"]);
    expect(options[0][1]).toBe("как на сервере · openai/gpt-oss-120b");
    expect(options[1][1]).toBe("gigachat · GigaChat-2-Max");
    expect(options[2][1]).toMatch(/^groq-20b · gpt-oss-20b — пауза до/);
  });

  it("сохранённое подключение, которого нет на сервере, — отдельной строкой", () => {
    expect(choiceOptions([conn()], server, "old").at(-1)).toEqual(["old", "old — нет на сервере"]);
    expect(choiceOptions([conn()], server, "GigaChat")).toHaveLength(2);
  });
});

describe("маршрут /llm", () => {
  it("подвкладка — второй сегмент пути", () => {
    expect(parseRoute("/llm/models")).toMatchObject({ name: "llm", sub: "models" });
    expect(parseRoute("/llm").name).toBe("llm");
  });
});
