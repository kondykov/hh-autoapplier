import { BackendClient, runtimeSender } from "../api/client";
import { loadConfig } from "../core/config";
import { Controller } from "../core/controller";
import { DailyLimit } from "../core/daily-limit";
import { StateRepository } from "../core/state";
import { ConsoleSink, Logger } from "../infra/logger";
import { ChromeStorage } from "../infra/storage";
import { chromeTab } from "../infra/tab";
import { PreparedLetterProvider, StaticTemplateProvider } from "../letters/letter-provider";
import { ResumeSelector } from "../resume/selector";
import { VERSION } from "../shared/build-info";
import type { PanelCommand } from "../shared/panel/model";
import { PageHost } from "../shared/panel/page-host";
import { isPanelRequest, type PanelBackgroundRequest, type PanelStateMessage } from "../shared/panel/protocol";
import { PanelStore } from "../shared/panel/store";
import { HH_VIEW } from "../pages/hh-view";

// Composition root: единственное место, где собираются зависимости
async function bootstrap() {
  if (document.getElementById("ap-panel-host")) return;

  const storage = new ChromeStorage();
  const config = await loadConfig(storage);
  const repo = new StateRepository(storage);
  const state = await repo.load();
  const limit = new DailyLimit(storage, config.dailyLimit);
  const logger = new Logger();
  logger.addSink(new ConsoleSink());
  // Письмо от backend под вакансию приоритетнее универсального из настроек
  const letters = new PreparedLetterProvider(new StaticTemplateProvider(() => config.coverLetterTemplate));
  const resumes = new ResumeSelector(logger);

  // Состояние панели — одно на вкладку; его рисуют панель на странице и боковая панель браузера (#123)
  const panel = new PanelStore(VERSION);
  logger.addSink(panel);

  const backend = new BackendClient(runtimeSender);
  const tab = await chromeTab();
  const controller = new Controller({ storage, config, state, repo, limit, logger, panel, letters, resumes, backend, tab });

  const toBackground = (action: PanelBackgroundRequest["action"]) =>
    chrome.runtime.sendMessage<PanelBackgroundRequest>({ type: "panel-bg", action }).catch(() => undefined);

  // Команды панели на странице. Открыть настройки и боковую панель может только фон
  const send = (command: PanelCommand) => {
    if (command.kind === "options.open") return void toBackground("open-options");
    if (command.kind === "mode" && command.mode === "side") void toBackground("open-side-panel");
    void controller.dispatch(command);
  };

  const host = new PageHost(storage, send, HH_VIEW);
  await host.mount();
  panel.subscribe((s) => {
    host.update(s);
    // Боковой панели: она показывает активную вкладку и сама отбирает её сообщения
    chrome.runtime.sendMessage<PanelStateMessage>({ type: "panel-state", state: s }).catch(() => undefined);
  });

  // Команды боковой панели: только от своего расширения. Ответ — состояние сразу, не дожидаясь долгих действий (очередь)
  chrome.runtime.onMessage.addListener((message: unknown, sender, sendResponse) => {
    if (sender.id !== chrome.runtime.id || !isPanelRequest(message)) return false;
    if (message.command) void controller.dispatch(message.command);
    sendResponse(panel.snapshot);
    return false;
  });

  await controller.init();
  // Индикатор связи и сводка очереди для панели на странице; в боковом режиме их проверяет боковая панель
  if (config.panelMode !== "side") void controller.settings.refreshServer(true);
  logger.log(`Панель загружена (v${VERSION})`);
}

void bootstrap();
