// Приёмы современного UI (#128): адрес «Вакансий», отмена тостом, клавиши «Проверки», Confirm
import { afterEach, describe, expect, it, vi } from "vitest";
import { bindReviewKeys, reviewKey } from "../src/pages/review-keys";
import { parseVacancyFilter, vacancyApiQuery, vacancyFilterPath, type VacancyFilter } from "../src/pages/vacancies";
import { Confirm, el, Toast } from "../src/ui/ui";
import { undoable } from "../src/undo";

afterEach(() => {
  document.body.replaceChildren();
});

const parse = (search: string) => parseVacancyFilter(new URLSearchParams(search));
const base: VacancyFilter = { view: "queued", status: "", search: "", hidden: false, skip: 0 };

describe("адрес «Вакансий»", () => {
  it("без параметров — «В очереди»", () => expect(parse("")).toEqual(base));

  it("вид, поиск, скрытые и страница", () => {
    expect(parse("view=invited&search=dotnet-remote&hidden=1&skip=50")).toEqual({ view: "invited", status: "", search: "dotnet-remote", hidden: true, skip: 50 });
  });

  it("статус — только в виде «Все»", () => {
    expect(parse("view=all&status=Skipped")).toEqual({ ...base, view: "all", status: "Skipped" });
    expect(parse("view=queued&status=Skipped").status).toBe("");
  });

  it.each([
    ["status=Queued", "queued", ""],
    ["status=Rejected", "rejected", ""],
    ["status=Failed", "all", "Failed"],
    ["status=Нечто", "queued", ""],
    ["view=нет", "queued", ""],
  ])("старая ссылка %s → вид %s", (q, view, status) => {
    expect(parse(q)).toMatchObject({ view, status });
  });

  it("мусор в skip — 0", () => {
    expect(parse("skip=-5").skip).toBe(0);
    expect(parse("skip=abc").skip).toBe(0);
  });

  it("сборка: только непустое, туда и обратно без потерь", () => {
    expect(vacancyFilterPath(base)).toBe("/vacancies?view=queued");
    const full: VacancyFilter = { view: "all", status: "Skipped", search: "C# и .NET", hidden: true, skip: 100 };
    const url = vacancyFilterPath(full);
    expect(url).toBe("/vacancies?view=all&status=Skipped&search=C%23+%D0%B8+.NET&hidden=1&skip=100");
    expect(parse(url.split("?")[1])).toEqual(full);
    expect(vacancyFilterPath({ ...base, status: "Skipped" })).toBe("/vacancies?view=queued");
  });

  it("запрос к API: статус вида или выбранный, скрытые", () => {
    expect(vacancyApiQuery({ ...base, view: "applied" }).get("status")).toBe("Applied");
    expect(vacancyApiQuery({ ...base, view: "all" }).has("status")).toBe(false);
    const q = vacancyApiQuery({ ...base, view: "all", status: "Failed", search: "x", hidden: true, skip: 50 });
    expect(Object.fromEntries(q)).toEqual({ skip: "50", take: "50", status: "Failed", search: "x", includeHidden: "true" });
  });
});

const undoButton = () => document.querySelector<HTMLButtonElement>(".toast button:not([aria-label])");

describe("отмена тостом", () => {
  it("действие сразу, «Отменить» вызывает обратное ровно один раз", async () => {
    const run = vi.fn(async () => null);
    const undo = vi.fn(async () => null);
    const onUndone = vi.fn();
    await undoable(run, { done: "скрыта", undo, undone: "возвращена", onUndone });
    expect(run).toHaveBeenCalledTimes(1);
    expect(undo).not.toHaveBeenCalled();
    const button = undoButton()!;
    expect(button.textContent).toContain("Отменить");
    button.click();
    button.click();
    await vi.waitFor(() => expect(onUndone).toHaveBeenCalledTimes(1));
    expect(undo).toHaveBeenCalledTimes(1);
    expect(document.body.textContent).toContain("возвращена");
  });

  it("действие упало — тоста с отменой нет", async () => {
    const undo = vi.fn(async () => null);
    const result = await undoable(async () => { throw new Error("409"); }, { done: "скрыта", undo, undone: "возвращена" });
    expect(result).toBeUndefined();
    expect(document.querySelector(".toast")).toBeNull();
  });

  it("Toast: двойное нажатие «Отменить» — одна отмена", async () => {
    const undo = vi.fn();
    Toast("Вакансия скрыта", { undo });
    const button = undoButton()!;
    button.click();
    button.click();
    await Promise.resolve();
    expect(undo).toHaveBeenCalledTimes(1);
  });
});

function reviewList() {
  const card = (n: number) => {
    const approve = el("button", { textContent: "Одобрить" });
    const decline = el("button", { textContent: "Отклонить" });
    approve.dataset.key = "approve";
    decline.dataset.key = "decline";
    const letter = el("textarea");
    letter.dataset.key = "letter";
    const reason = el("input");
    return el("article", { className: "card", tabIndex: -1, "data-n": n }, letter, reason, approve, decline);
  };
  const list = el("div", {}, card(1), card(2), card(3));
  document.body.append(list);
  bindReviewKeys(list);
  return list;
}
const press = (target: EventTarget, code: string, extra: KeyboardEventInit = {}) =>
  target.dispatchEvent(new KeyboardEvent("keydown", { code, key: code.replace("Key", "").toLowerCase(), bubbles: true, cancelable: true, ...extra }));

describe("клавиши «Проверки»", () => {
  it("в полях ввода и с модификаторами — не команды", () => {
    for (const tag of ["input", "textarea", "select"] as const) expect(reviewKey({ code: "KeyA", ctrlKey: false, metaKey: false, altKey: false, target: el(tag) })).toBeNull();
    expect(reviewKey({ code: "KeyA", ctrlKey: true, metaKey: false, altKey: false, target: document.body })).toBeNull();
    expect(reviewKey({ code: "KeyA", ctrlKey: false, metaKey: false, altKey: false, target: document.body })).toBe("approve");
  });

  it("J/K двигают текущую карточку, A одобряет текущую и ведёт к следующей", () => {
    const list = reviewList();
    const approve = vi.fn();
    list.querySelector<HTMLButtonElement>("[data-n='2'] [data-key=approve]")!.onclick = approve;
    press(document.body, "KeyJ");
    press(document.body, "KeyJ");
    expect(list.querySelector(".current")?.getAttribute("data-n")).toBe("2");
    press(document.body, "KeyA");
    expect(approve).toHaveBeenCalledTimes(1);
    expect(list.querySelector(".current")?.getAttribute("data-n")).toBe("3");
    press(document.body, "KeyK");
    expect(list.querySelector(".current")?.getAttribute("data-n")).toBe("2");
  });

  it("A и R в поле письма или причины — просто буквы", () => {
    const list = reviewList();
    const approve = vi.fn();
    const decline = vi.fn();
    list.querySelectorAll<HTMLButtonElement>("[data-key=approve]").forEach((b) => (b.onclick = approve));
    list.querySelectorAll<HTMLButtonElement>("[data-key=decline]").forEach((b) => (b.onclick = decline));
    press(document.body, "KeyJ");
    const letter = list.querySelector<HTMLTextAreaElement>(".current [data-key=letter]")!;
    expect(press(letter, "KeyA")).toBe(true);
    expect(press(list.querySelector(".current input")!, "KeyR")).toBe(true);
    expect(approve).not.toHaveBeenCalled();
    expect(decline).not.toHaveBeenCalled();
  });

  it("E — фокус на письмо текущей карточки", () => {
    const list = reviewList();
    press(document.body, "KeyJ");
    press(document.body, "KeyE");
    expect(document.activeElement).toBe(list.querySelector(".current [data-key=letter]"));
  });
});

describe("Confirm", () => {
  it("подтверждение — true, отмена и Esc — false", async () => {
    const yes = Confirm({ title: "Удалить поиск?", confirm: "Удалить", kind: "danger" });
    document.querySelector<HTMLFormElement>(".dialog")!.requestSubmit();
    expect(await yes).toBe(true);
    expect(document.querySelector(".dialog-backdrop")).toBeNull();

    const no = Confirm({ title: "Отозвать токен?" });
    [...document.querySelectorAll<HTMLButtonElement>(".dialog button")].find((b) => b.textContent === "Отмена")!.click();
    expect(await no).toBe(false);

    const esc = Confirm({ title: "Отвязать Telegram?" });
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
    expect(await esc).toBe(false);
  });

  it("клавиши «Проверки» молчат, пока открыт диалог", () => {
    void Confirm({ title: "?" });
    expect(reviewKey({ code: "KeyA", ctrlKey: false, metaKey: false, altKey: false, target: document.body })).toBeNull();
  });
});
