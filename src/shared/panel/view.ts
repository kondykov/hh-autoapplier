import { PANEL_MODES, sectionsFor, type PanelCommand, type PanelSection, type PanelState } from "./model";
import { PANEL_CSS } from "./theme";

// Вид панели (#123): один для боковой панели браузера и для панели на странице. Рисует PanelState,
// действия отдаёт командами — что за ними стоит, решает владелец состояния (content script страницы)

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
  readonly el = document.createElement("div");

  private state: PanelState | null = null;

  constructor(
    private send: (command: PanelCommand) => void,
    private options: ViewOptions,
  ) {}

  // Стили — в тот же root: в shadow root страницы сайта стили документа не действуют
  mount(root: Root): void {
    const style = document.createElement("style");
    style.textContent = PANEL_CSS;
    this.el.className = "ap-root";
    this.el.innerHTML = this.template();
    root.append(style, this.el);
    this.bind();
  }

  // Элементы вида: data-ap="…"
  $<T extends HTMLElement = HTMLElement>(name: string): T {
    return this.el.querySelector<T>(`[data-ap="${name}"]`)!;
  }

  private template(): string {
    const [mark, accent] = this.options.brand;
    const stat = (name: string, label: string) => `<div class="ap-stat"><b data-ap="${name}">—</b><span>${label}</span></div>`;
    const modes = PANEL_MODES.map((m) => `<button type="button" data-ap-mode="${m.mode}" title="${m.title}">${m.label}</button>`).join("");
    return `
      <div class="ap-hd">
        <span class="ap-mark">${mark}<b>${accent}</b></span>
        <span class="ap-title" data-ap="title"></span>
        <span class="ap-ver" data-ap="version" title="Версия расширения"></span>
        <span class="ap-spacer"></span>
        <button type="button" class="ap-link" data-ap="server" title="Проверить связь с сервером"><span class="ap-dot" data-ap="server-dot"></span><span data-ap="server-label"></span></button>
        <button type="button" class="ap-link" data-ap="options" title="Настройки расширения">⚙</button>
        <button type="button" class="ap-link" data-ap="collapse" title="Свернуть в плашку" hidden>—</button>
      </div>
      <div class="ap-modes" data-ap="modes" role="group" aria-label="Вид панели">${modes}</div>
      <div class="ap-banner" data-ap="captcha" hidden>Капча: работа на паузе — введите текст с картинки на странице</div>
      <div class="ap-confirm" data-ap="confirm" hidden>
        <div data-ap="confirm-text"></div>
        <div class="ap-row"><button type="button" class="ap-btn warn" data-ap="confirm-yes"></button><button type="button" class="ap-btn" data-ap="confirm-no">Отмена</button></div>
      </div>
      <div class="ap-body">
        <div class="ap-step" data-ap="step"></div>
        <section class="ap-sec" data-ap-section="openSite">
          <h4>Эта вкладка</h4>
          <div class="ap-hint" style="margin:0 0 10px">Откройте ${this.options.site} — здесь появятся действия для открытой страницы.</div>
          <button type="button" class="ap-btn" data-ap="site-open">Открыть ${this.options.site}</button>
        </section>
        <section class="ap-sec" data-ap-section="sendSearch">
          <h4>Эта страница</h4>
          <button type="button" class="ap-btn primary" data-ap="search-send"></button>
          <div class="ap-hint" data-ap="search-hint"></div>
        </section>
        <section class="ap-sec" data-ap-section="queue">
          <h4>Очередь</h4>
          <div class="ap-stats">${stat("q-queued", "в очереди")}${stat("q-review", "проверка")}${stat("q-run", "за запуск")}${stat("q-daily", "за день")}</div>
          <button type="button" class="ap-btn" data-ap="queue-btn"></button>
          <label class="ap-check"><input type="checkbox" data-ap="dryrun" /> Пробный режим — не нажимать «Отправить»</label>
        </section>
        <section class="ap-sec" data-ap-section="syncChats">
          <h4>Чаты</h4>
          <button type="button" class="ap-btn primary" data-ap="chats-sync">Синхронизировать чаты с сервером</button>
          <div class="ap-hint">Статусы, приглашения и отказы — на сервер; разбор ответов — в Telegram и веб-интерфейсе.</div>
          <div class="ap-hint" data-ap="thanks"></div>
        </section>
        <section class="ap-sec" data-ap-section="syncChatsLink">
          <button type="button" class="ap-btn" data-ap="chats-sync-link">Синхронизировать чаты</button>
          <div class="ap-hint">Откроет страницу откликов и чатов и передаст их на сервер.</div>
        </section>
        <section class="ap-sec" data-ap-section="oldTools">
          <details class="ap-old"><summary>Старые инструменты</summary>
            <div class="ap-hint" style="margin:0 0 8px">Открывают чаты с отказами по одному; отказы теперь разбирает сервер.</div>
            <div class="ap-row"><button type="button" class="ap-btn" data-ap="rej-next">Next Отказ</button><button type="button" class="ap-btn" data-ap="rej-auto">Авто-отказ</button></div>
          </details>
        </section>
      </div>
      <details class="ap-log" data-ap="log"${this.options.logOpen ? " open" : ""}><summary>Журнал<span class="ap-log-last" data-ap="log-last"></span></summary><div class="ap-log-lines" data-ap="log-lines"></div></details>`;
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
    for (const button of Array.from(this.el.querySelectorAll<HTMLButtonElement>("[data-ap-mode]")))
      button.onclick = () => this.send({ kind: "mode", mode: button.dataset.apMode as PanelState["mode"] });

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
    for (const button of Array.from(this.el.querySelectorAll<HTMLButtonElement>("[data-ap-mode]")))
      button.setAttribute("aria-pressed", String(button.dataset.apMode === state.mode));

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
    text("search-hint", search ? "Сервер отберёт и оценит вакансии, отклик — из очереди" : "Задайте поиск для выдачи в настройках (⚙)");

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
    box.replaceChildren(...state.log.map((line) => {
      const row = document.createElement("div");
      row.className = line.level;
      const time = document.createElement("time");
      time.textContent = fmtTime(line.at);
      row.append(time, line.text);
      return row;
    }));
    const last = state.log.at(-1);
    this.$("log-last").textContent = last ? `· ${last.text}` : "";
    if (atBottom) box.scrollTop = box.scrollHeight;
  }
}
