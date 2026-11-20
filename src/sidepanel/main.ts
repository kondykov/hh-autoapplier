import { BackendClient, runtimeSender } from "../api/client";
import { fetchLink } from "../api/server-link";
import { loadConfig, saveConfig, watchConfig } from "../core/config";
import { ChromeStorage } from "../infra/storage";
import { chatsPageUrl, requestChatSync } from "../pages/chat-sync";
import { HH_HOME, HH_VIEW } from "../pages/hh-view";
import { VERSION } from "../shared/build-info";
import { appendLog, emptyState, type LogLine, type PanelCommand, type PanelMode, type PanelState, type ServerLink, type ServerQueue } from "../shared/panel/model";
import { isPanelState, type PanelRequest } from "../shared/panel/protocol";
import { PanelView } from "../shared/panel/view";

// Боковая панель браузера (#123): показывает состояние активной вкладки hh.ru и отправляет в неё команды.
// Логика остаётся в content script вкладки — режим панели меняет только вид. Связь с сервером панель проверяет сама:
// индикатор нужен и тогда, когда hh.ru не открыт

const storage = new ChromeStorage();
const backend = new BackendClient(runtimeSender);

let tabId: number | null = null;
// Состояние вкладки; null — во вкладке нет нашей страницы
let tabState: PanelState | null = null;
let link: { link: ServerLink; queue: ServerQueue | null } | null = null;
let localLog: LogLine[] = [];
let mode: PanelMode = "side";
let serverKey = "";

const view = new PanelView((command) => void handle(command), { ...HH_VIEW, logOpen: true });
view.mount(document.getElementById("app")!);

function log(text: string, level: LogLine["level"] = "info") {
  localLog = appendLog(localLog, { at: Date.now(), level, text });
  render();
}

function render() {
  const base = tabState ?? { ...emptyState(VERSION), pageTitle: "Нет страницы hh.ru" };
  const merged = [...base.log, ...localLog].sort((a, b) => a.at - b.at).slice(-50);
  view.update({
    ...base,
    version: VERSION,
    mode,
    server: link?.link ?? base.server,
    serverQueue: link?.queue ?? base.serverQueue,
    log: merged,
  });
}

async function ask(command?: PanelCommand): Promise<PanelState | null> {
  if (tabId === null) return null;
  try {
    const state = await chrome.tabs.sendMessage<PanelRequest, PanelState | undefined>(tabId, { type: "panel-request", command });
    return state ?? null;
  } catch {
    // Нет content script: другой сайт, служебная страница или вкладка открыта до установки расширения
    return null;
  }
}

async function refreshTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  tabId = tab?.id ?? null;
  tabState = await ask();
  render();
}

async function refreshServer(quiet: boolean) {
  if (!quiet) {
    link = { link: { state: "checking", label: "проверяю", detail: "Проверяю связь с сервером…" }, queue: link?.queue ?? null };
    render();
  }
  link = await fetchLink(backend, quiet);
  if (!quiet) log(link.link.detail, link.link.state === "ok" ? "success" : "warn");
  else render();
}

async function handle(command: PanelCommand) {
  switch (command.kind) {
    case "options.open":
      await chrome.runtime.openOptionsPage();
      return;
    case "site.open":
      await chrome.tabs.create({ url: HH_HOME });
      return;
    case "server.check":
      return refreshServer(false);
    case "mode": {
      // Вид запоминается в настройках; вкладки hh.ru узнают о нём сами (watchConfig)
      const config = await loadConfig(storage);
      config.panelMode = command.mode;
      await saveConfig(storage, config);
      mode = command.mode;
      render();
      return;
    }
    case "chats.sync":
      if (tabState) break;
      // hh.ru в этой вкладке не открыт: открыть отклики в новой вкладке, сбор запустится после загрузки
      await requestChatSync(storage);
      await chrome.tabs.create({ url: chatsPageUrl() });
      log("Открываю страницу откликов и чатов hh.ru для синхронизации", "step");
      return;
    default:
      break;
  }
  if (!tabState) {
    log("Откройте hh.ru в этой вкладке", "warn");
    return;
  }
  tabState = (await ask(command)) ?? tabState;
  render();
  if (command.kind === "search.send") setTimeout(() => void refreshServer(true), 3000);
}

// Состояние своей вкладки: от content script, только от своего расширения
chrome.runtime.onMessage.addListener((message: unknown, sender) => {
  if (sender.id !== chrome.runtime.id || !isPanelState(message)) return false;
  if (sender.tab?.id !== undefined && sender.tab.id === tabId) {
    tabState = message.state;
    render();
  }
  return false;
});

async function start() {
  const config = await loadConfig(storage);
  mode = config.panelMode;
  serverKey = JSON.stringify(config.server);
  const windowId = (await chrome.windows.getCurrent()).id;
  chrome.tabs.onActivated.addListener((info) => {
    if (info.windowId === windowId) void refreshTab();
  });
  // Переход в той же вкладке: новая страница пришлёт состояние сама, но вкладка могла уйти с hh.ru
  chrome.tabs.onUpdated.addListener((id, change) => {
    if (id === tabId && change.status === "complete") void refreshTab();
  });
  // Настройки поменяли на странице настроек или в другой вкладке: вид панели; новый адрес или токен — проверить связь
  watchConfig(storage, (next) => {
    mode = next.panelMode;
    const key = JSON.stringify(next.server);
    if (key !== serverKey) {
      serverKey = key;
      void refreshServer(true);
    } else render();
  });

  await refreshTab();
  void refreshServer(true);
  // Сводка очереди меняется на сервере: обновлять, пока панель открыта
  setInterval(() => void refreshServer(true), 60_000);
}

void start();
