import type { PanelCommand, PanelState } from "./model";

// Сообщения между страницей сайта (content script), боковой панелью и фоном (#123).
// Принимаются только от своего расширения: проверка sender.id — у получателя

// Страница → все страницы расширения: новое состояние своей вкладки (боковая панель показывает активную вкладку)
export interface PanelStateMessage {
  type: "panel-state";
  state: PanelState;
}

// Боковая панель → страница во вкладке: прислать состояние или выполнить команду; ответ — состояние после неё
export interface PanelRequest {
  type: "panel-request";
  command?: PanelCommand;
}

// Страница → фон: то, что content script сам сделать не может
export interface PanelBackgroundRequest {
  type: "panel-bg";
  action: "open-side-panel" | "open-options";
}

const typed = (m: unknown, type: string) => typeof m === "object" && m !== null && (m as { type?: unknown }).type === type;

export const isPanelState = (m: unknown): m is PanelStateMessage => typed(m, "panel-state");
export const isPanelRequest = (m: unknown): m is PanelRequest => typed(m, "panel-request");
export const isPanelBackgroundRequest = (m: unknown): m is PanelBackgroundRequest => typed(m, "panel-bg");
