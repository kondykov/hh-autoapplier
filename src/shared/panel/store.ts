import type { LogLevel, LogSink } from "../../infra/logger";
import { appendLog, emptyState, type PageKind, type PanelMode, type PanelState, type ServerLink, type ServerQueue } from "./model";

export interface PanelSettings {
  dryRun: boolean;
  dailyLimit: number;
  runLimit: number;
  importSearch: string;
  thanksOnReject: boolean;
  mode: PanelMode;
}

export interface PanelStats {
  daily: number;
  dailyLimit: number;
  runApplied: number;
  runLimit: number;
}

// Состояние панели на странице сайта (#123). Шаги и настройки пишут сюда, виды (панель на странице, боковая панель)
// подписываются и рисуют. Подписчики узнают об изменениях пачкой — один раз за такт, а не на каждый вызов
export class PanelStore implements LogSink {
  private state: PanelState;

  private listeners: ((state: PanelState) => void)[] = [];

  private scheduled = false;

  constructor(version: string, private now: () => number = Date.now) {
    this.state = emptyState(version);
  }

  get snapshot(): PanelState {
    return this.state;
  }

  subscribe(listener: (state: PanelState) => void): void {
    this.listeners.push(listener);
    listener(this.state);
  }

  private patch(change: Partial<PanelState>): void {
    this.state = { ...this.state, ...change };
    if (this.scheduled) return;
    this.scheduled = true;
    queueMicrotask(() => {
      this.scheduled = false;
      for (const listener of this.listeners) listener(this.state);
    });
  }

  private patchQueue(change: Partial<PanelState["queue"]>): void {
    this.patch({ queue: { ...this.state.queue, ...change } });
  }

  setPage(page: PageKind, pageTitle: string): void {
    this.patch({ page, pageTitle });
  }

  setStep(step: string): void {
    this.patch({ step });
  }

  // elsewhere — очередь ведёт другая вкладка
  setQueueActive(running: boolean, elsewhere = false): void {
    this.patchQueue({ running, elsewhere: running && elsewhere });
  }

  setRejectActive(rejectsActive: boolean): void {
    this.patch({ rejectsActive });
  }

  setCaptcha(captcha: boolean): void {
    this.patch({ captcha });
  }

  setServer(server: ServerLink, serverQueue?: ServerQueue | null): void {
    this.patch(serverQueue === undefined ? { server } : { server, serverQueue });
  }

  setStats(stats: PanelStats): void {
    this.patchQueue(stats);
  }

  setSettings(s: PanelSettings): void {
    this.patchQueue({ dryRun: s.dryRun, dailyLimit: s.dailyLimit, runLimit: s.runLimit });
    this.patch({ importSearch: s.importSearch, thanksOnReject: s.thanksOnReject, mode: s.mode });
  }

  write(text: string, level: LogLevel): void {
    this.patch({ log: appendLog(this.state.log, { at: this.now(), level, text }) });
  }
}
