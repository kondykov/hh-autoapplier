/// <reference types="vite/client" />
import { afterEach, describe, expect, it, vi } from "vitest";
import { Button, Card, el, safeUrl, StackTable, Toast } from "../src/ui";
import KIT from "../src/ui.ts?raw";

afterEach(() => {
  document.body.replaceChildren();
  vi.useRealTimers();
});

describe("el и safeUrl", () => {
  it("данные — только текстом", () => {
    const node = el("div", {}, "<img src=x onerror=alert(1)>", 5, null, false, ["a", ["b"]]);
    expect(node.querySelector("img")).toBeNull();
    expect(node.textContent).toBe("<img src=x onerror=alert(1)>5ab");
  });

  it("ссылки — только http(s)", () => {
    expect(safeUrl("javascript:alert(1)")).toBe("#");
    expect(safeUrl("не адрес")).toBe("#");
    expect(safeUrl("https://hh.ru/vacancy/1")).toBe("https://hh.ru/vacancy/1");
    const card = Card({ title: "Вакансия", href: "javascript:alert(1)" });
    expect(card.querySelector("a")?.getAttribute("href")).toBe("#");
  });
});

describe("StackTable", () => {
  it("подпись поля в каждой ячейке — для карточек на телефоне; числа — справа", () => {
    const table = StackTable([{ title: "Поиск" }, { title: "Собрано", num: true }], [["dotnet", 20], ["backend", 0]]);
    const cells = [...table.querySelectorAll("tbody td")];
    expect(cells.map((td) => td.getAttribute("data-label"))).toEqual(["Поиск", "Собрано", "Поиск", "Собрано"]);
    expect(cells.map((td) => td.textContent)).toEqual(["dotnet", "20", "backend", "0"]);
    expect(table.querySelector("th.n")?.textContent).toBe("Собрано");
    expect(cells[1].className).toBe("n");
  });

  it("пустая ячейка и узел", () => {
    const table = StackTable([{ title: "" }, { title: "Действие" }], [[null, Button("Отозвать")]]);
    const [empty, action] = table.querySelectorAll("td");
    expect(empty.childNodes).toHaveLength(0);
    expect(action.querySelector("button")?.textContent).toBe("Отозвать");
  });
});

describe("Toast", () => {
  it("«Отменить» вызывается один раз и закрывает тост", async () => {
    const undo = vi.fn();
    const toast = Toast("Вакансия скрыта", { undo, timeout: 0 });
    expect(document.querySelector(".toasts")?.contains(toast)).toBe(true);
    const button = [...toast.querySelectorAll("button")].find((b) => b.textContent === "Отменить");
    button?.click();
    await Promise.resolve();
    expect(undo).toHaveBeenCalledTimes(1);
    expect(toast.isConnected).toBe(false);
  });

  it("без отмены — только «Закрыть»; закрывается сам по таймеру", () => {
    vi.useFakeTimers();
    const toast = Toast("Сохранено", { tone: "ok", timeout: 1000 });
    expect(toast.className).toBe("toast ok");
    expect(toast.querySelectorAll("button")).toHaveLength(1);
    vi.advanceTimersByTime(1000);
    expect(toast.isConnected).toBe(false);
  });

  it("контейнер один на root", () => {
    Toast("1", { timeout: 0 });
    Toast("2", { timeout: 0 });
    expect(document.querySelectorAll(".toasts")).toHaveLength(1);
    expect(document.querySelectorAll(".toast")).toHaveLength(2);
  });
});

describe("Button", () => {
  it("занята, пока идёт действие", async () => {
    let finish!: () => void;
    const b = Button("Сохранить", { onClick: () => new Promise<void>((r) => (finish = r)) });
    b.click();
    expect(b.disabled).toBe(true);
    expect(b.getAttribute("aria-busy")).toBe("true");
    finish();
    await new Promise((r) => setTimeout(r));
    expect(b.disabled).toBe(false);
  });
});

// UI kit общий для веба и всех расширений (#129, #131): своего состояния и чужого кода в нём нет
describe("Граница UI kit", () => {
  it("ничего не импортирует: в нём нет кода приложений", () => {
    expect(KIT.length).toBeGreaterThan(0);
    expect(KIT).not.toMatch(/^\s*import\s/m);
  });
});
