// Шапка и палитра команд (#128): фильтр, права, флаг поиска вакансий, Ctrl+K / Esc, знак «!» у LLM
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Vacancy } from "../src/api/types";
import { llmAlert, paletteGroups, vacancySource, type CommandActions } from "../src/commands";
import { CommandPalette, filterPalette, Tabs, type PaletteGroup } from "../src/ui/ui";

const actions: CommandActions = { theme: { label: "Тема: тёмная", icon: "moon", run: () => {} }, logout: () => {}, collect: () => {}, checkLlm: () => {} };
const labels = (groups: PaletteGroup[]) => groups.flatMap((g) => g.items.map((i) => i.label));
const key = (k: string, init: KeyboardEventInit = {}) => new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true, ...init });

afterEach(() => {
  document.body.replaceChildren();
});

describe("filterPalette", () => {
  const groups: PaletteGroup[] = [
    { title: "Разделы", items: [{ label: "Дашборд" }, { label: "LLM · Отсев", keywords: "rejects" }, { label: "Поиски" }] },
    { title: "Команды", items: [{ label: "Тёмная тема" }] },
  ];

  it("пустой запрос — всё", () => {
    expect(filterPalette(groups, "  ")).toEqual(groups);
  });
  it("каждое слово — подстрока подписи или keywords; без регистра, ё = е; пустые группы пропадают", () => {
    expect(labels(filterPalette(groups, "llm отс"))).toEqual(["LLM · Отсев"]);
    expect(labels(filterPalette(groups, "REJECTS"))).toEqual(["LLM · Отсев"]);
    expect(filterPalette(groups, "темная").map((g) => g.title)).toEqual(["Команды"]);
    expect(filterPalette(groups, "нет такого")).toEqual([]);
  });
});

describe("paletteGroups — права", () => {
  it("пользователь: без «Системы», «Подключений», сбора и проверки LLM", () => {
    const all = labels(paletteGroups(false, actions));
    expect(all).toContain("LLM · Отсев");
    expect(all).toContain("Выйти");
    for (const admin of ["Система", "LLM · Подключения", "Запустить сбор вакансий", "Проверить LLM"]) expect(all).not.toContain(admin);
  });
  it("администратор: всё", () => {
    const all = labels(paletteGroups(true, actions));
    for (const admin of ["Система", "LLM · Подключения", "Запустить сбор вакансий", "Проверить LLM"]) expect(all).toContain(admin);
  });
});

describe("vacancySource — за флагом", () => {
  const list = [{ title: "C# разработчик", company: "Облако-9", url: "https://hh.ru/vacancy/1" }, { title: "PHP-разработчик", company: "Диджитал", url: "https://hh.ru/vacancy/2" }] as Vacancy[];

  it("флаг выключен (по умолчанию) — источника нет, группа «Вакансии» не появится", () => {
    expect(vacancySource(async () => list)).toEqual([]);
  });
  it("флаг включён — фильтр по названию и компании, загрузка одна на палитру", async () => {
    const load = vi.fn(async () => list);
    const [source] = vacancySource(load, true);
    expect(source.title).toBe("Вакансии");
    expect((await source.search("c# облако")).map((i) => i.label)).toEqual(["C# разработчик"]);
    expect((await source.search("php")).map((i) => i.href)).toEqual(["https://hh.ru/vacancy/2"]);
    expect(load).toHaveBeenCalledTimes(1);
  });
});

describe("CommandPalette", () => {
  const groups = () => paletteGroups(false, actions);

  it("Ctrl+K открывает с фокусом в поле, Esc закрывает и возвращает фокус", () => {
    const before = document.createElement("button");
    document.body.append(before);
    before.focus();
    const palette = CommandPalette({ groups });
    expect(palette.isOpen).toBe(false);
    document.dispatchEvent(key("k", { ctrlKey: true, code: "KeyK" }));
    expect(palette.isOpen).toBe(true);
    expect(palette.querySelector("[role=dialog]")?.getAttribute("aria-modal")).toBe("true");
    expect(document.activeElement).toBe(palette.querySelector("input"));
    document.dispatchEvent(key("Escape"));
    expect(palette.isOpen).toBe(false);
    expect(document.activeElement).toBe(before);
    // Русская раскладка: код клавиши тот же
    document.dispatchEvent(key("л", { metaKey: true, code: "KeyK" }));
    expect(palette.isOpen).toBe(true);
    palette.dispose();
  });

  it("ввод фильтрует, ↑↓ выбирают по кругу, Enter выполняет и закрывает; клик мимо закрывает", () => {
    const run = vi.fn();
    const palette = CommandPalette({ groups: () => [{ title: "Команды", items: [{ label: "Первая", onRun: run }, { label: "Вторая", onRun: run }, { label: "Другое" }] }] });
    palette.open();
    const input = palette.querySelector("input")!;
    input.value = "ая";
    input.dispatchEvent(new Event("input"));
    const options = () => [...palette.querySelectorAll<HTMLElement>("[role=option]")];
    expect(options().map((o) => o.textContent)).toEqual(["Первая", "Вторая"]);
    expect(options()[0].getAttribute("aria-selected")).toBe("true");
    input.dispatchEvent(key("ArrowDown"));
    expect(input.getAttribute("aria-activedescendant")).toBe(options()[1].id);
    input.dispatchEvent(key("ArrowDown"));
    expect(options()[0].getAttribute("aria-selected")).toBe("true");
    input.dispatchEvent(key("ArrowUp"));
    input.dispatchEvent(key("Enter"));
    expect(run).toHaveBeenCalledTimes(1);
    expect(palette.isOpen).toBe(false);
    palette.open();
    palette.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    expect(palette.isOpen).toBe(false);
    palette.dispose();
  });

  it("тексты пунктов — только текстом", () => {
    const palette = CommandPalette({ groups: () => [{ title: "Вакансии", items: [{ label: "<img src=x onerror=alert(1)>", href: "javascript:alert(1)" }] }] });
    palette.open();
    expect(palette.querySelector("img")).toBeNull();
    expect(palette.querySelector("a")?.getAttribute("href")).not.toContain("javascript");
    palette.dispose();
  });
});

describe("«!» у LLM", () => {
  it("по «Требует внимания» обзора: пауза — warn, сбой связи или лимит — bad, отсев и письма — нет", () => {
    expect(llmAlert(null)).toBeNull();
    expect(llmAlert({ attention: [{ kind: "rejects", text: "27 пограничных", href: null }] })).toBeNull();
    expect(llmAlert({ attention: [{ kind: "pause", text: "Groq на паузе", href: null }] })).toEqual({ tone: "warn", title: "Groq на паузе" });
    expect(llmAlert({ attention: [{ kind: "pause", text: "a", href: null }, { kind: "link", text: "b", href: null }] })?.tone).toBe("bad");
    expect(llmAlert({ attention: [{ kind: "limit", text: "лимит", href: null }] })?.tone).toBe("bad");
  });
  it("в разделах — знак с причиной в aria-label", () => {
    const tabs = Tabs([{ href: "/llm", label: "LLM", alert: { tone: "warn", title: "Groq на паузе" } }, { href: "/searches", label: "Поиски" }]);
    const alerts = tabs.querySelectorAll(".alert");
    expect(alerts).toHaveLength(1);
    expect(alerts[0].textContent).toBe("!");
    expect(alerts[0].getAttribute("aria-label")).toContain("Groq на паузе");
  });
});
