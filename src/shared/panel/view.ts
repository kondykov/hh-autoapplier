import { Button, Callout, el, Segmented, Stat, type Child } from "../../../web/src/ui/ui";
import { mountKit } from "../kit";
import { PANEL_MODES, sectionsFor, type PanelCommand, type PanelMode, type PanelSection, type PanelState } from "./model";
import PANEL_CSS from "./panel.css";

// Вид панели (#123): один для боковой панели браузера и для панели на странице. Рисует PanelState,
// действия отдаёт командами — что за ними стоит, решает владелец состояния (content script страницы).
// Узлы, стили и иконки — из UI kit (#129): Button, Segmented, Stat, Callout, Icon; своё — только раскладка (panel.css)

export interface ViewOptions {
  // Знак в шапке: «hh» + акцентная часть
  brand: [string, string];
  // Название сайта для подсказок: «hh.ru»
  site: string;
  // Журнал раскрыт сразу (в боковой панели места хватает)
  logOpen?: boolean;
  // Кнопка «свернуть» в шапке — только у панели на странице
  onCollapse?: () => void;
}

type Root = HTMLElement | ShadowRoot;

const fmtTime = (at: number) => new Date(at).toLocaleTimeString("ru-RU", { hour: "2-digit", minute: "2-digit" });

export class PanelView {
  readonly el = el("div", { className: "ap-root" });

  private state: PanelState | null = null;

  constructor(
    private send: (command: PanelCommand) => void,
    private options: ViewOptions,
  ) {}

  // Стили kit и панели — в тот же root: в shadow root страницы сайта стили документа не действуют
  mount(root: Root): void {
    const target = root instanceof ShadowRoot ? root : root.getRootNode();
    if (target instanceof ShadowRoot || target instanceof Document) mountKit(target, PANEL_CSS);
    this.el.replaceChildren(...this.build());
    root.append(this.el);
    this.bind();
  }

  // Элементы вида: data-ap="…"
  $<T extends HTMLElement = HTMLElement>(name: string): T {
    return this.el.querySelector<T>(`[data-ap="${name}"]`)!;
  }

  private build(): Node[] {
    const [mark, accent] = this.options.brand;
    const site = this.options.site;
    const ap = (name: string) => ({ "data-ap": name });
    const tag = <T extends HTMLElement>(node: T, name: string): T => {
      node.dataset.ap = name;
      return node;
    };
    const button = (name: string, text: string, kind?: "primary") => tag(Button(text, { kind, className: "ap-wide" }), name);
    const section = (name: PanelSection, ...children: Child[]) => el("section", { className: "ap-sec", "data-ap-section": name }, children);
    const hint = (text: string, name?: string) => el("div", { className: "ap-hint", textContent: text, ...(name ? ap(name) : {}) });
    const stat = (name: string, label: string) => {
      const tile = Stat({ value: "—", label });
      tag(tile.querySelector("b")!, name);
      return tile;
    };

    const modes = Segmented(PANEL_MODES.map((m) => ({ value: m.mode, label: m.label })), "side" as PanelMode,
      (mode) => this.send({ kind: "mode", mode }), { label: "Вид панели" });
    Array.from(modes.querySelectorAll("button")).forEach((b, i) => {
      b.dataset.apMode = PANEL_MODES[i].mode;
      b.title = PANEL_MODES[i].title;
    });

    const server = tag(Button("", { kind: "ghost", title: "Проверить связь с сервером", className: "ap-server" }), "server");
    server.append(el("span", { className: "ap-dot", ...ap("server-dot") }), el("span", ap("server-label")));
    const collapse = tag(Button("Свернуть в плашку", { kind: "ghost", icon: "chevron", iconOnly: true }), "collapse");
    collapse.hidden = true;

    const captcha = tag(Callout("Капча: работа на паузе — введите текст с картинки на странице"), "captcha");
    captcha.classList.add("ap-banner");
    captcha.hidden = true;

    return [
      el("div", { className: "ap-hd" },
        el("span", { className: "ap-mark" }, mark, el("b", { textContent: accent })),
        el("span", { className: "ap-title", ...ap("title") }),
        el("span", { className: "ap-ver", title: "Версия расширения", ...ap("version") }),
        el("span", { className: "ap-spacer" }),
        server,
        tag(Button("Настройки расширения", { kind: "ghost", icon: "admin", iconOnly: true }), "options"),
        collapse),
      el("div", { className: "ap-modes", ...ap("modes") }, modes),
      captcha,
      el("div", { className: "card tight ap-confirm", hidden: true, ...ap("confirm") },
        el("div", ap("confirm-text")),
        el("div", { className: "actions" }, tag(Button("", { kind: "danger" }), "confirm-yes"), tag(Button("Отмена"), "confirm-no"))),
      el("div", { className: "ap-body" },
        el("div", { className: "ap-step", ...ap("step") }),
        section("openSite", el("h3", { textContent: "Эта вкладка" }),
          el("div", { className: "ap-hint ap-lead", textContent: `Откройте ${site} — здесь появятся действия для открытой страницы.` }),
          button("site-open", `Открыть ${site}`)),
        section("sendSearch", el("h3", { textContent: "Эта страница" }),
          button("search-send", "", "primary"), hint("", "search-hint")),
        section("queue", el("h3", { textContent: "Очередь" }),
          el("div", { className: "kpis" }, stat("q-queued", "в очереди"), stat("q-review", "проверка"), stat("q-run", "за запуск"), stat("q-daily", "за день")),
          button("queue-btn", ""),
          el("label", { className: "ap-check" }, el("input", { type: "checkbox", ...ap("dryrun") }), "Пробный режим — не нажимать «Отправить»")),
        section("syncChats", el("h3", { textContent: "Чаты" }),
          button("chats-sync", "Синхронизировать чаты с сервером", "primary"),
          hint("Статусы, приглашения и отказы — на сервер; разбор ответов — в Telegram и веб-интерфейсе."),
          hint("", "thanks")),
        section("syncChatsLink",
          button("chats-sync-link", "Синхронизировать чаты"),
          hint("Откроет страницу откликов и чатов и передаст их на сервер.")),
        section("oldTools",
          el("details", { className: "ap-old" }, el("summary", { textContent: "Старые инструменты" }),
            el("div", { className: "ap-hint ap-lead", textContent: "Открывают чаты с отказами по одному; отказы теперь разбирает сервер." }),
            el("div", { className: "ap-row" }, button("rej-next", "Next Отказ"), button("rej-auto", "Авто-отказ"))))),
      el("details", { className: "ap-log", open: !!this.options.logOpen, ...ap("log") },
        el("summary", {}, "Журнал", el("span", { className: "ap-log-last", ...ap("log-last") })),
        el("div", { className: "ap-log-lines", ...ap("log-lines") })),
    ];
  }

  private bind(): void {
    const click = (name: string, command: () => PanelCommand) => (this.$(name).onclick = () => this.send(command()));
    click("server", () => ({ kind: "server.check" }));
    click("options", () => ({ kind: "options.open" }));
    click("site-open", () => ({ kind: "site.open" }));
    click("search-send", () => ({ kind: "search.send" }));
    click("queue-btn", () => ({ kind: "queue.toggle" }));
    click("chats-sync", () => ({ kind: "chats.sync" }));
    click("chats-sync-link", () => ({ kind: "chats.sync" }));
    click("rej-next", () => ({ kind: "rejects.next" }));
    click("rej-auto", () => ({ kind: "rejects.auto" }));

    const collapse = this.$("collapse");
    collapse.hidden = !this.options.onCollapse;
    collapse.onclick = () => this.options.onCollapse?.();

    // Выключение пробного режима — переход к настоящим откликам, только с подтверждением.
    // Панель может жить в DOM страницы сайта: снять режим может только действие человека (event.isTrusted), не скрипт страницы
    const dryRun = this.$<HTMLInputElement>("dryrun");
    dryRun.onchange = async (event) => {
      if (dryRun.checked) {
        this.send({ kind: "queue.dryRun", enabled: true });
        return;
      }
      dryRun.checked = true;
      if (!event.isTrusted) return;
      if (!(await this.ask("Очередь начнёт отправлять отклики по-настоящему. Выключить пробный режим?", "Да, отправлять"))) return;
      dryRun.checked = false;
      this.send({ kind: "queue.dryRun", enabled: false });
    };
  }

  // Подтверждение в панели вместо confirm(). Новый вопрос отменяет предыдущий неотвеченный
  private pendingAnswer: ((yes: boolean) => void) | null = null;

  ask(message: string, yesLabel = "Да"): Promise<boolean> {
    this.pendingAnswer?.(false);
    const box = this.$("confirm");
    this.$("confirm-text").textContent = message;
    this.$("confirm-yes").textContent = yesLabel;
    box.hidden = false;
    return new Promise((resolve) => {
      const answer = (yes: boolean) => {
        this.pendingAnswer = null;
        box.hidden = true;
        resolve(yes);
      };
      this.pendingAnswer = answer;
      // Отвечает только человек: программный click() скрипта страницы игнорируется
      const onlyTrusted = (yes: boolean) => (event: Event) => {
        if (event.isTrusted) answer(yes);
      };
      this.$("confirm-yes").onclick = onlyTrusted(true);
      this.$("confirm-no").onclick = onlyTrusted(false);
    });
  }

  update(state: PanelState): void {
    const prev = this.state;
    this.state = state;
    const text = (name: string, value: string) => (this.$(name).textContent = value);

    text("title", state.pageTitle || this.options.site);
    text("version", `v${state.version}`);
    const dot = this.$("server-dot");
    dot.className = `ap-dot ${state.server.state}`;
    text("server-label", state.server.label);
    this.$("server").title = `${state.server.detail}\nНажмите, чтобы проверить связь`;
    for (const button of Array.from(this.el.querySelectorAll<HTMLButtonElement>("[data-ap-mode]"))) {
      const on = button.dataset.apMode === state.mode;
      button.classList.toggle("on", on);
      button.setAttribute("aria-pressed", String(on));
    }

    this.$("captcha").hidden = !state.captcha;
    text("step", state.step);

    const shown = new Set<PanelSection>(sectionsFor(state.page, state.queue.running));
    for (const section of Array.from(this.el.querySelectorAll<HTMLElement>("[data-ap-section]")))
      section.hidden = !shown.has(section.dataset.apSection as PanelSection);

    // Выдача → поиск на сервере: без имени поиска отправлять некуда
    const search = state.importSearch.trim();
    const send = this.$<HTMLButtonElement>("search-send");
    send.textContent = search ? `Отправить страницу в поиск «${search}»` : "Отправить страницу в поиск";
    send.disabled = !search;
    text("search-hint", search ? "Сервер отберёт и оценит вакансии, отклик — из очереди" : "Задайте поиск для выдачи в настройках расширения");

    const q = state.queue;
    const sq = state.serverQueue;
    text("q-queued", sq ? String(sq.queued) : "—");
    text("q-review", sq ? String(sq.review) : "—");
    text("q-run", q.runLimit > 0 ? `${q.runApplied}/${q.runLimit}` : String(q.runApplied));
    text("q-daily", `${q.daily}/${q.dailyLimit}`);
    const queueBtn = this.$<HTMLButtonElement>("queue-btn");
    queueBtn.textContent = !q.running ? "Запустить очередь" : q.elsewhere ? "Остановить очередь (идёт в другой вкладке)" : "Остановить очередь";
    queueBtn.classList.toggle("primary", q.running);
    // Пробный режим: включение — сразу, выключение — через вопрос; внешнее изменение не сбрасывает открытый вопрос
    const dryRun = this.$<HTMLInputElement>("dryrun");
    if (!prev || prev.queue.dryRun !== q.dryRun) dryRun.checked = q.dryRun;

    text("thanks", `«Спасибо» на новые отказы: ${state.thanksOnReject ? "включено" : "выключено"} (в настройках)`);
    this.$("rej-auto").textContent = state.rejectsActive ? "Стоп отказ" : "Авто-отказ";

    this.renderLog(state);
  }

  private renderLog(state: PanelState): void {
    const box = this.$("log-lines");
    const atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 8;
    box.replaceChildren(...state.log.map((line) => el("div", { className: line.level }, el("time", { textContent: fmtTime(line.at) }), line.text)));
    const last = state.log.at(-1);
    this.$("log-last").textContent = last ? `· ${last.text}` : "";
    if (atBottom) box.scrollTop = box.scrollHeight;
  }
}
