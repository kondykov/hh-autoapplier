// Шапка и палитра команд (#128): фильтр, права, флаг поиска вакансий, Ctrl+K / Esc, знак «!» у LLM
import { afterEach, describe, expect, it, vi } from "vitest";
import type { Vacancy } from "../src/api/types";
import { llmAlert, paletteGroups, vacancyHref, vacancySource, type CommandActions } from "../src/commands";
import { features } from "../src/config";
import { parseVacancyFilter, vacancyApiQuery } from "../src/pages/vacancies";
import { CommandPalette, filterPalette, Tabs, type PaletteGroup, type PaletteItem } from "@hh-autoapplier/ui";

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

describe("vacancySource — серверный поиск (#141)", () => {
  const list = [
    { id: "1", title: "C# разработчик", company: "Облако-9", url: "https://hh.ru/vacancy/1", status: "Applied" },
    { id: "2", title: "PHP-разработчик", company: "Диджитал", url: "https://hh.ru/vacancy/2", status: "Review" },
    { id: "3", title: "Тестировщик", company: "Облако-9", url: "https://hh.ru/vacancy/3", status: "Skipped", hiddenAt: "2026-11-01T00:00:00Z" },
  ] as Vacancy[];
  const signal = new AbortController().signal;

  it("флаг включён по умолчанию; выключен — источника нет", () => {
    expect(features.vacancySearch).toBe(true);
    expect(vacancySource(async () => list)).toHaveLength(1);
    expect(vacancySource(async () => list, false)).toEqual([]);
  });
  it("запрос — серверу как есть, с signal; пункт ведёт в «Вакансии» или в «Проверку»", async () => {
    const find = vi.fn(async () => list);
    const [source] = vacancySource(find, true);
    expect(source.title).toBe("Вакансии");
    const items = await source.search("облако", signal);
    expect(find).toHaveBeenCalledWith("облако", signal);
    expect(items.map((i) => i.label)).toEqual(["C# разработчик", "PHP-разработчик", "Тестировщик"]);
    expect(items[0].hint).toBe("Облако-9 · отклик отправлен");
    expect(items.map((i) => i.href)).toEqual([
      "/vacancies?view=all&status=Applied&q=C%23+%D1%80%D0%B0%D0%B7%D1%80%D0%B0%D0%B1%D0%BE%D1%82%D1%87%D0%B8%D0%BA",
      "/review",
      `/vacancies?view=all&status=Skipped&q=${encodeURIComponent("Тестировщик")}&hidden=1`,
    ]);
    expect(items.every((i) => !i.external)).toBe(true);
  });
  it("ссылка из палитры открывает «Вакансии» с тем же текстом и статусом", () => {
    const href = vacancyHref(list[0]);
    expect(parseVacancyFilter(new URL(href, "http://x").searchParams)).toMatchObject({ view: "all", status: "Applied", q: "C# разработчик", hidden: false });
    expect(Object.fromEntries(vacancyApiQuery(parseVacancyFilter(new URL(href, "http://x").searchParams)))).toMatchObject({ status: "Applied", q: "C# разработчик" });
  });
  it("не больше 8 пунктов; пустой ответ — пусто", async () => {
    const many = Array.from({ length: 20 }, (_, i) => ({ ...list[0], id: String(i) }));
    expect(await vacancySource(async () => many, true)[0].search("c#", signal)).toHaveLength(8);
    expect(await vacancySource(async () => [], true)[0].search("нет", signal)).toEqual([]);
  });
});

describe("CommandPalette — источник с запросом", () => {
  afterEach(() => vi.useRealTimers());
  const type = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event("input")); };

  it("запрос — после паузы ввода 200 мс, короче 2 символов — без запроса; прошлый запрос отменяется", async () => {
    vi.useFakeTimers();
    const signals: AbortSignal[] = [];
    const search = vi.fn((q: string, signal: AbortSignal) => { signals.push(signal); return new Promise<PaletteItem[]>(() => {}); });
    const palette = CommandPalette({ groups: () => [], sources: [{ title: "Вакансии", search }] });
    palette.open();
    const input = palette.querySelector("input")!;
    type(input, "c");
    await vi.advanceTimersByTimeAsync(300);
    expect(search).not.toHaveBeenCalled();
    type(input, "c#");
    type(input, "c# р");
    await vi.advanceTimersByTimeAsync(199);
    expect(search).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(search).toHaveBeenCalledTimes(1);
    expect(search).toHaveBeenLastCalledWith("c# р", expect.anything());
    // Новый ввод — прошлый запрос отменён, новый — после паузы
    type(input, "c# ра");
    expect(signals[0].aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(200);
    expect(search).toHaveBeenCalledTimes(2);
    // Закрытие отменяет идущий запрос
    palette.close();
    expect(signals[1].aborted).toBe(true);
    palette.dispose();
  });

  it("найденное — группой «Вакансии»; пустой ответ — «Ничего не найдено»; ответ на устаревший запрос не рисуется", async () => {
    vi.useFakeTimers();
    let answer: (items: PaletteItem[]) => void = () => {};
    const search = vi.fn((q: string) => q === "пусто" ? Promise.resolve([]) : new Promise<PaletteItem[]>((r) => { answer = r; }));
    const palette = CommandPalette({ groups: () => [], sources: [{ title: "Вакансии", search }] });
    palette.open();
    const input = palette.querySelector("input")!;
    type(input, "разработчик");
    await vi.advanceTimersByTimeAsync(200);
    const stale = answer;
    type(input, "тестировщик");
    await vi.advanceTimersByTimeAsync(200);
    answer([{ label: "Тестировщик", href: "/vacancies?q=x" }]);
    await vi.advanceTimersByTimeAsync(0);
    stale([{ label: "Устаревшее", href: "/vacancies" }]);
    await vi.advanceTimersByTimeAsync(0);
    expect(palette.querySelector(".palette-caption")?.textContent).toBe("Вакансии");
    expect([...palette.querySelectorAll("[role=option]")].map((o) => o.textContent)).toEqual(["Тестировщик"]);
    type(input, "пусто");
    await vi.advanceTimersByTimeAsync(200);
    expect(palette.querySelector("[role=option]")).toBeNull();
    expect(palette.querySelector(".palette-empty")?.textContent).toBe("Ничего не найдено");
    palette.dispose();
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
