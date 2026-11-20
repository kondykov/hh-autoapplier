import type { KeyValueStorage } from "../../infra/storage";
import type { PanelCommand, PanelState } from "./model";
import { PanelView, type ViewOptions } from "./view";

// Панель на странице сайта (#123): режимы «плавающая» и «плашка». В режиме «боковая» на странице ничего нет.
// Всё — в закрытом shadow root: стили страницы не ломают панель, скрипты страницы не читают её содержимое

const POS_KEY = "panel_pos";

// Плашка — в левом нижнем углу: правый нижний занимают кнопка и окно чатов hh.ru.
// Цвета и тени — токены UI kit (#129): kit вставляется в тот же shadow root (PanelView.mount), тема — по системе
const HOST_CSS = `
:host{all:initial}
.ap-float{position:fixed;top:72px;right:16px;width:340px;max-height:min(560px,calc(100vh - 96px));z-index:2147483647;display:flex;flex-direction:column;
  border:1px solid var(--border);border-radius:var(--r-lg);overflow:hidden;box-shadow:var(--shadow-pop)}
.ap-float .ap-root{flex:1}
.ap-float .ap-hd{cursor:move;user-select:none}
.ap-pill{position:fixed;left:16px;bottom:16px;z-index:2147483647;display:flex;align-items:center;gap:8px;padding:7px 12px;border-radius:var(--r-pill);
  background:var(--card);border:1px solid var(--border);color:var(--text);font:600 var(--fs-xs) var(--mono);cursor:pointer;box-shadow:var(--shadow-pop)}
.ap-pill:hover:not(:disabled){border-color:var(--muted)}
.ap-pill .ap-mark{border:1px solid var(--border);border-radius:var(--r-sm);padding:0 5px;background:var(--bg)}
.ap-pill b{color:var(--accent)}
.ap-pill .alert-text{background:var(--bad);color:var(--on-accent);border-radius:var(--r-pill);padding:0 7px}
.ap-pill[hidden],.ap-float[hidden]{display:none}
`;

// Текст плашки: самое важное — капча и идущая очередь, иначе сводка очереди сервера
export function pillText(state: PanelState): string {
  if (state.captcha) return "капча — нужна помощь";
  const q = state.queue;
  if (q.running) return `очередь идёт · ${q.runLimit > 0 ? `${q.runApplied}/${q.runLimit}` : q.runApplied}`;
  if (state.serverQueue) return `очередь ${state.serverQueue.queued} · проверка ${state.serverQueue.review}`;
  return "панель";
}

export class PageHost {
  private host = document.createElement("div");

  private shadow: ShadowRoot;

  private float = document.createElement("div");

  private pill = document.createElement("button");

  private view: PanelView;

  private state: PanelState | null = null;

  // Плашку раскрыли кликом: до сворачивания показываем плавающую панель
  private expanded = false;

  constructor(
    private storage: KeyValueStorage,
    send: (command: PanelCommand) => void,
    private options: Omit<ViewOptions, "onCollapse">,
    // В тестах — открытый shadow root, чтобы видеть содержимое
    shadowMode: ShadowRootMode = "closed",
  ) {
    this.shadow = this.host.attachShadow({ mode: shadowMode });
    this.view = new PanelView(send, { ...options, onCollapse: () => this.collapse(send) });
  }

  get panel(): PanelView {
    return this.view;
  }

  async mount(): Promise<void> {
    this.host.id = "ap-panel-host";
    const style = document.createElement("style");
    style.textContent = HOST_CSS;
    this.float.className = "ap-float";
    this.pill.className = "ap-pill";
    this.pill.type = "button";
    this.pill.title = "Открыть панель";
    this.pill.onclick = () => {
      this.expanded = true;
      this.render();
    };
    this.shadow.append(style, this.float, this.pill);
    this.view.mount(this.float);
    document.body.append(this.host);
    this.enableDrag();
    await this.restorePosition();
  }

  // Свернуть: из плавающего режима — переключить режим на плашку, из раскрытой плашки — просто свернуть
  private collapse(send: (command: PanelCommand) => void): void {
    if (this.state?.mode === "floating") send({ kind: "mode", mode: "pill" });
    this.expanded = false;
    this.render();
  }

  update(state: PanelState): void {
    if (this.state?.mode !== state.mode) this.expanded = false;
    this.state = state;
    this.render();
  }

  private render(): void {
    const state = this.state;
    if (!state) return;
    const floating = state.mode === "floating" || (state.mode === "pill" && this.expanded);
    this.float.hidden = !floating;
    this.pill.hidden = state.mode !== "pill" || this.expanded;
    if (floating) this.view.update(state);
    if (!this.pill.hidden) {
      const [mark, accent] = this.options.brand;
      this.pill.replaceChildren();
      const logo = document.createElement("span");
      logo.className = "ap-mark";
      const b = document.createElement("b");
      b.textContent = accent;
      logo.append(mark, b);
      const text = document.createElement("span");
      text.textContent = pillText(state);
      text.className = state.captcha ? "alert-text" : "";
      this.pill.append(logo, text);
    }
  }

  private enableDrag(): void {
    const header = this.view.el.querySelector<HTMLElement>(".ap-hd")!;
    header.addEventListener("mousedown", (down) => {
      if ((down.target as HTMLElement).closest("button")) return;
      const rect = this.float.getBoundingClientRect();
      const dx = down.clientX - rect.left;
      const dy = down.clientY - rect.top;
      const move = (e: MouseEvent) => {
        // Не уводить шапку за край окна: иначе панель не вернуть
        const left = Math.min(Math.max(0, e.clientX - dx), window.innerWidth - 120);
        const top = Math.min(Math.max(0, e.clientY - dy), window.innerHeight - 40);
        Object.assign(this.float.style, { left: `${left}px`, top: `${top}px`, right: "auto" });
      };
      const up = () => {
        document.removeEventListener("mousemove", move);
        document.removeEventListener("mouseup", up);
        void this.storage.set(POS_KEY, { left: this.float.style.left, top: this.float.style.top });
      };
      document.addEventListener("mousemove", move);
      document.addEventListener("mouseup", up);
    });
  }

  private async restorePosition(): Promise<void> {
    const pos = await this.storage.get<{ left: string; top: string } | null>(POS_KEY, null);
    if (pos) Object.assign(this.float.style, { left: pos.left, top: pos.top, right: "auto" });
  }
}
