import { beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryStorage } from "../src/infra/storage";
import { pageKindOf } from "../src/pages/page-kind";
import { appendLog, emptyState, LOG_LIMIT, sectionsFor, type PanelCommand, type PanelState } from "../src/shared/panel/model";
import { PageHost, pillText } from "../src/shared/panel/page-host";
import { PanelStore } from "../src/shared/panel/store";
import { PanelView } from "../src/shared/panel/view";

const tick = () => new Promise((r) => setTimeout(r, 0));

const state = (patch: Partial<PanelState> = {}): PanelState => ({ ...emptyState("dev"), page: "other", ...patch });

function mount(initial: PanelState = state()) {
  const sent: PanelCommand[] = [];
  const view = new PanelView((c) => sent.push(c), { brand: ["hh", "=#"], site: "hh.ru" });
  const root = document.createElement("div");
  document.body.append(root);
  view.mount(root);
  view.update(initial);
  return { view, sent };
}

// Действие человека: у программных click()/dispatchEvent isTrusted === false, подделать его может только тест
function trusted<T extends Event>(event: T): T {
  Object.defineProperty(event, "isTrusted", { value: true });
  return event;
}

const press = (el: HTMLElement) => el.dispatchEvent(trusted(new MouseEvent("click", { bubbles: true })));

function uncheck(box: HTMLInputElement) {
  box.checked = false;
  box.dispatchEvent(trusted(new Event("change")));
}

const visibleSections = (view: PanelView) =>
  Array.from(view.el.querySelectorAll<HTMLElement>("[data-ap-section]")).filter((s) => !s.hidden).map((s) => s.dataset.apSection);

beforeEach(() => {
  document.body.innerHTML = "";
});

describe("Действия по странице (#123)", () => {
  it("выдача: отправить в поиск, очередь, синхронизация чатов ссылкой", () => {
    expect(sectionsFor("search")).toEqual(["sendSearch", "queue", "syncChatsLink"]);
  });

  it("вакансия и отклик: очередь", () => {
    expect(sectionsFor("vacancy")).toEqual(["queue", "syncChatsLink"]);
  });

  it("чаты: синхронизация главным действием, старые инструменты свёрнуты, очереди нет", () => {
    expect(sectionsFor("chat")).toEqual(["syncChats", "oldTools"]);
  });

  it("идущую очередь видно и на чатах — её можно остановить", () => {
    expect(sectionsFor("chat", true)).toEqual(["queue", "syncChats", "oldTools"]);
  });

  it("вкладка без hh.ru: открыть сайт и синхронизировать чаты", () => {
    expect(sectionsFor("none")).toEqual(["openSite", "syncChatsLink"]);
    expect(sectionsFor("none", true)).toEqual(["openSite", "syncChatsLink"]);
  });

  it("тип страницы hh.ru → раздел панели", () => {
    expect(pageKindOf("search")).toEqual({ kind: "search", title: "Выдача" });
    expect(pageKindOf("vacancy").kind).toBe("vacancy");
    expect(pageKindOf("response")).toEqual({ kind: "vacancy", title: "Отклик" });
    expect(pageKindOf("chat")).toEqual({ kind: "chat", title: "Чаты" });
    expect(pageKindOf("unknown").kind).toBe("other");
  });

  it("вид показывает только разделы страницы", () => {
    const { view } = mount(state({ page: "search", importSearch: "dotnet" }));
    expect(visibleSections(view)).toEqual(["sendSearch", "queue", "syncChatsLink"]);
    view.update(state({ page: "chat" }));
    expect(visibleSections(view)).toEqual(["syncChats", "oldTools"]);
  });

  it("на кнопке выдачи — имя поиска; без поиска кнопка неактивна", () => {
    const { view, sent } = mount(state({ page: "search", importSearch: "dotnet-remote" }));
    const button = view.$<HTMLButtonElement>("search-send");
    expect(button.textContent).toBe("Отправить страницу в поиск «dotnet-remote»");
    button.click();
    expect(sent).toEqual([{ kind: "search.send" }]);

    view.update(state({ page: "search", importSearch: "" }));
    expect(button.disabled).toBe(true);
  });

  it("кнопки отдают команды: очередь, чаты, проверка связи, вид, настройки, старые инструменты", () => {
    const { view, sent } = mount();
    view.$("queue-btn").click();
    view.$("chats-sync-link").click();
    view.$("server").click();
    view.$("options").click();
    view.$("rej-auto").click();
    view.el.querySelector<HTMLButtonElement>('[data-ap-mode="pill"]')!.click();
    expect(sent.map((c) => c.kind)).toEqual(["queue.toggle", "chats.sync", "server.check", "options.open", "rejects.auto", "mode"]);
    expect(sent.at(-1)).toEqual({ kind: "mode", mode: "pill" });
  });

  it("очередь: прогресс, лимиты и сводка сервера; кнопка — запуск или остановка", () => {
    const { view } = mount(state({
      page: "vacancy",
      serverQueue: { queued: 2, review: 7 },
      queue: { running: true, elsewhere: false, dryRun: true, runApplied: 3, runLimit: 10, daily: 5, dailyLimit: 148 },
    }));
    expect(view.$("q-queued").textContent).toBe("2");
    expect(view.$("q-review").textContent).toBe("7");
    expect(view.$("q-run").textContent).toBe("3/10");
    expect(view.$("q-daily").textContent).toBe("5/148");
    expect(view.$("queue-btn").textContent).toBe("Остановить очередь");

    view.update(state({ page: "vacancy" }));
    expect(view.$("queue-btn").textContent).toBe("Запустить очередь");
    expect(view.$("q-queued").textContent).toBe("—");
  });

  it("отмеченный вид панели и индикатор связи в шапке", () => {
    const { view } = mount(state({ mode: "floating", server: { state: "ok", label: "0.4.1", detail: "Связь есть", contract: 1 } }));
    expect(view.el.querySelector('[data-ap-mode="floating"]')!.getAttribute("aria-pressed")).toBe("true");
    expect(view.el.querySelector('[data-ap-mode="side"]')!.getAttribute("aria-pressed")).toBe("false");
    expect(view.$("server-label").textContent).toBe("0.4.1");
    expect(view.$("server-dot").className).toContain("ok");
    expect(view.$("version").textContent).toBe("vdev");
  });
});

describe("Пробный режим в панели", () => {
  it("выключается только после подтверждения в панели, без confirm()", async () => {
    const confirmSpy = vi.spyOn(window, "confirm");
    const { view, sent } = mount();
    const box = view.$<HTMLInputElement>("dryrun");
    expect(box.checked).toBe(true);

    uncheck(box);
    expect(box.checked).toBe(true);
    expect(view.$("confirm").hidden).toBe(false);
    expect(sent).toEqual([]);

    press(view.$("confirm-yes"));
    await tick();
    expect(box.checked).toBe(false);
    expect(view.$("confirm").hidden).toBe(true);
    expect(sent).toEqual([{ kind: "queue.dryRun", enabled: false }]);
    expect(confirmSpy).not.toHaveBeenCalled();

    box.checked = true;
    box.dispatchEvent(new Event("change"));
    expect(sent.at(-1)).toEqual({ kind: "queue.dryRun", enabled: true });
  });

  it("«Отмена» оставляет пробный режим", async () => {
    const { view, sent } = mount();
    const box = view.$<HTMLInputElement>("dryrun");
    uncheck(box);
    press(view.$("confirm-no"));
    await tick();
    expect(box.checked).toBe(true);
    expect(sent).toEqual([]);
  });

  it("скрипт страницы не снимает пробный режим и не подтверждает вопрос", async () => {
    const { view, sent } = mount();
    const box = view.$<HTMLInputElement>("dryrun");

    box.checked = false;
    box.dispatchEvent(new Event("change"));
    await tick();
    expect(box.checked).toBe(true);
    expect(view.$("confirm").hidden).toBe(true);

    uncheck(box);
    view.$("confirm-yes").click();
    await tick();
    expect(view.$("confirm").hidden).toBe(false);
    expect(sent).toEqual([]);
  });

  it("новый вопрос отменяет прежний", async () => {
    const { view } = mount();
    const first = view.ask("Первый?", "Да");
    const second = view.ask("Второй?", "Перейти");
    expect(await first).toBe(false);
    expect(view.$("confirm-yes").textContent).toBe("Перейти");
    press(view.$("confirm-yes"));
    expect(await second).toBe(true);
  });

  it("состояние из другой вкладки ставит галочку", () => {
    const { view } = mount();
    view.update(state({ queue: { ...state().queue, dryRun: false } }));
    expect(view.$<HTMLInputElement>("dryrun").checked).toBe(false);
  });
});

describe("Журнал", () => {
  it("ограничен по числу строк", () => {
    let log = emptyState("dev").log;
    for (let i = 0; i < LOG_LIMIT + 5; i++) log = appendLog(log, { at: i, level: "info", text: `строка ${i}` });
    expect(log).toHaveLength(LOG_LIMIT);
    expect(log[0].text).toBe("строка 5");
  });

  it("свёрнут на странице, в заголовке — последняя строка", () => {
    const { view } = mount(state({ log: [{ at: 0, level: "success", text: "Чатов: 12" }] }));
    expect(view.$<HTMLDetailsElement>("log").open).toBe(false);
    expect(view.$("log-last").textContent).toBe("· Чатов: 12");
    expect(view.$("log-lines").children).toHaveLength(1);
  });
});

describe("PanelStore", () => {
  it("сообщает подписчикам одной пачкой и копит журнал", async () => {
    const store = new PanelStore("dev", () => 1000);
    const seen: PanelState[] = [];
    store.subscribe((s) => seen.push(s));
    store.setStep("шаг");
    store.setQueueActive(true, true);
    store.write("готово", "success");
    expect(seen).toHaveLength(1);
    await tick();
    expect(seen).toHaveLength(2);
    expect(seen[1].step).toBe("шаг");
    expect(seen[1].queue).toMatchObject({ running: true, elsewhere: true });
    expect(seen[1].log).toEqual([{ at: 1000, level: "success", text: "готово" }]);
  });

  it("остановленная очередь не бывает «в другой вкладке»", () => {
    const store = new PanelStore("dev");
    store.setQueueActive(false, true);
    expect(store.snapshot.queue.elsewhere).toBe(false);
  });
});

describe("Панель на странице: режимы", () => {
  async function host(mode: PanelState["mode"]) {
    const sent: PanelCommand[] = [];
    const h = new PageHost(new MemoryStorage(), (c) => sent.push(c), { brand: ["hh", "=#"], site: "hh.ru" }, "open");
    await h.mount();
    h.update(state({ mode, serverQueue: { queued: 2, review: 7 } }));
    const shadow = document.getElementById("ap-panel-host")!.shadowRoot!;
    return { h, sent, float: shadow.querySelector<HTMLElement>(".ap-float")!, pill: shadow.querySelector<HTMLButtonElement>(".ap-pill")! };
  }

  it("боковая — на странице ничего", async () => {
    const { float, pill } = await host("side");
    expect(float.hidden).toBe(true);
    expect(pill.hidden).toBe(true);
  });

  it("плавающая — панель; «свернуть» переключает на плашку", async () => {
    const { h, float, pill, sent } = await host("floating");
    expect(float.hidden).toBe(false);
    expect(pill.hidden).toBe(true);
    h.panel.$("collapse").click();
    expect(sent).toEqual([{ kind: "mode", mode: "pill" }]);
  });

  it("плашка раскрывается по клику и сворачивается обратно без смены режима", async () => {
    const { h, float, pill, sent } = await host("pill");
    expect(pill.hidden).toBe(false);
    expect(pill.textContent).toContain("очередь 2 · проверка 7");
    pill.click();
    expect(float.hidden).toBe(false);
    expect(pill.hidden).toBe(true);
    h.panel.$("collapse").click();
    expect(float.hidden).toBe(true);
    expect(pill.hidden).toBe(false);
    expect(sent).toEqual([]);
  });

  it("плашка: капча и идущая очередь важнее сводки", () => {
    expect(pillText(state({ captcha: true }))).toBe("капча — нужна помощь");
    expect(pillText(state({ queue: { ...state().queue, running: true, runApplied: 3, runLimit: 10 } }))).toBe("очередь идёт · 3/10");
    expect(pillText(state())).toBe("панель");
  });
});
