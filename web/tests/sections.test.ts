// Ревизия разделов (#128): «Требует внимания» на дашборде и меню аккаунта (Menu из kit)
import { describe, expect, it } from "vitest";
import { attentionItems } from "../src/pages/attention";
import { Menu } from "@hh-autoapplier/ui";

describe("attentionItems", () => {
  const telegram = { configured: true, linked: false } as Parameters<typeof attentionItems>[1];

  it("пункты по ненулевым счётчикам, со ссылками на разделы", () => {
    const items = attentionItems({ Review: 3, Failed: 1, Skipped: 0, Queued: 5 }, telegram, false, 2);
    expect(items.map((i) => i.href)).toEqual(["/review", "/vacancies?status=Failed", "/vacancies?status=Queued", "/dialogs", "/account?section=telegram"]);
    expect(items[0].text).toBe("На проверке 3");
    expect(items[3].text).toContain("необработанных 2");
  });

  it("Telegram: не настроен, привязан или чужие данные — не показывается", () => {
    const has = (t: Parameters<typeof attentionItems>[1], readOnly = false) => attentionItems({}, t, readOnly).some((i) => i.text.startsWith("Telegram"));
    expect(has(telegram)).toBe(true);
    expect(has({ ...telegram!, linked: true })).toBe(false);
    expect(has({ ...telegram!, configured: false })).toBe(false);
    expect(has(null)).toBe(false);
    expect(has(telegram, true)).toBe(false);
  });

  it("ответы работодателей — всегда", () => {
    expect(attentionItems({}, null, false).map((i) => i.href)).toEqual(["/dialogs"]);
  });
});

describe("Menu", () => {
  it("открывается кнопкой, закрывается выбором пункта и Esc; keepOpen — остаётся открытым", async () => {
    let picked = 0;
    const menu = Menu("Анна", [{ label: "Тема", keepOpen: true, onClick: () => { picked++; } }, "sep", { label: "Выйти", onClick: () => { picked++; } }]);
    document.body.append(menu);
    const list = menu.querySelector<HTMLElement>(".menu-list")!;
    const [theme, logout] = menu.querySelectorAll<HTMLButtonElement>(".item");
    expect(list.hidden).toBe(true);
    menu.querySelector<HTMLButtonElement>(".menu-trigger")!.click();
    expect(list.hidden).toBe(false);
    theme.click();
    await Promise.resolve();
    expect(list.hidden).toBe(false);
    logout.click();
    await Promise.resolve();
    expect(picked).toBe(2);
    expect(list.hidden).toBe(true);
    menu.open();
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
    expect(list.hidden).toBe(true);
    expect(menu.querySelectorAll(".menu-sep")).toHaveLength(1);
    menu.dispose();
  });
});
