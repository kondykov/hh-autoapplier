import { executeApiRequest, isApiRequest, type ApiResponse } from "../api/transport";
import { loadConfig } from "../core/config";
import { ChromeStorage } from "../infra/storage";
import { isTabRequest, type TabResponse } from "../infra/tab";
import { isPanelBackgroundRequest } from "../shared/panel/protocol";

// Значок расширения открывает боковую панель (#123)
void chrome.sidePanel?.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => undefined);

// Фоновый service worker: единственное место, откуда расширение ходит в backend.
// Сообщения принимаются только от своего расширения (onMessage, не onMessageExternal)
chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse: (response: ApiResponse | TabResponse | { ok: boolean; error?: string }) => void) => {
  if (sender.id !== chrome.runtime.id) return false;

  // id вкладки и проверка вкладки-владельца очереди (#51); chrome.tabs.get без разрешения tabs — только id и статус.
  // Ответ асинхронный (return true), как у запросов к backend
  if (isTabRequest(message)) {
    const self = sender.tab?.id ?? null;
    const check = message.check;
    void (async () => {
      if (check === undefined) return sendResponse({ self });
      const open = await chrome.tabs.get(check).then(() => true, () => false);
      sendResponse({ self, open });
    })();
    return true;
  }

  // Панель на странице (#123): открыть настройки или боковую панель — content script этих API не имеет.
  // sidePanel.open Chrome разрешает только в ответ на действие пользователя: если жест не дошёл — подсказка в ответе
  if (isPanelBackgroundRequest(message)) {
    void (async () => {
      try {
        if (message.action === "open-options") await chrome.runtime.openOptionsPage();
        else if (sender.tab?.id !== undefined) await chrome.sidePanel.open({ tabId: sender.tab.id });
        sendResponse({ ok: true });
      } catch (e) {
        sendResponse({ ok: false, error: (e as Error).message });
      }
    })();
    return true;
  }

  if (!isApiRequest(message)) return false;
  void (async () => {
    const config = await loadConfig(new ChromeStorage());
    const hasAccess = (pattern: string) => chrome.permissions.contains({ origins: [pattern] });
    const response = await executeApiRequest(message, config.server, fetch, hasAccess);
    // Разрешение на адрес в сети даёт только страница настроек: запросить его из content script на hh.ru нельзя (#101).
    // Уже открытую страницу Chrome не дублирует, а выводит вперёд
    if (response.noAccess && !message.quiet) void chrome.runtime.openOptionsPage();
    sendResponse(response);
  })();
  return true; // ответ асинхронный
});
