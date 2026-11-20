import type { BackendClient } from "../api/client";
import { pageArchiveChecker, type ArchiveChecker } from "../apply/archive-check";
import { ApplyPipeline } from "../apply/pipeline";
import { CaptchaGuard } from "../captcha/guard";
import type { KeyValueStorage } from "../infra/storage";
import type { TabInfo } from "../infra/tab";
import type { Logger } from "../infra/logger";
import type { LetterProvider } from "../letters/letter-provider";
import { DialogStep } from "../pages/dialog-step";
import { QueueStep } from "../pages/queue-step";
import { RejectStep } from "../pages/reject-step";
import { SearchStep } from "../pages/search-step";
import { browserNavigator, pageOf, pickStep, stopQueue, type Navigator, type StepContext } from "../pages/step-context";
import { isDryRun, isQueueOn } from "../queue/queue-mode";
import type { ResumeSelector } from "../resume/selector";
import { chatsPageUrl, requestChatSync, takeChatSync } from "../pages/chat-sync";
import { pageKindOf } from "../pages/page-kind";
import type { PanelCommand } from "../shared/panel/model";
import type { PanelStore } from "../shared/panel/store";
import { watchConfig, type AppConfig } from "./config";
import type { DailyLimit } from "./daily-limit";
import { panelSettings, Settings } from "./settings";
import type { AppState, StateRepository } from "./state";

interface ControllerDeps {
  storage: KeyValueStorage;
  config: AppConfig;
  state: AppState;
  repo: StateRepository;
  limit: DailyLimit;
  logger: Logger;
  panel: PanelStore;
  letters: LetterProvider;
  resumes: ResumeSelector;
  backend: BackendClient;
  nav?: Navigator;
  tab?: TabInfo;
  archive?: ArchiveChecker;
}

// Без service worker (тесты, старый фон): очередь не привязана к вкладке
const ANY_TAB: TabInfo = { self: null, isOpen: async () => true };

// Сборка: общий контекст, шаги страниц, настройки и отчёты. После загрузки страницы управление получает подходящий шаг
export class Controller {
  readonly search: SearchStep;

  readonly queue: QueueStep;

  readonly rejects: RejectStep;

  readonly dialogs: DialogStep;

  readonly settings: Settings;


  private readonly captcha: CaptchaGuard;

  private readonly ctx: StepContext;

  constructor(private d: ControllerDeps) {
    this.captcha = new CaptchaGuard((shown) => {
      d.panel.setCaptcha(shown);
      d.logger.log(shown ? "Капча: работа на паузе" : d.state.queueRunning ? "Капча решена, продолжаю" : "Капча решена", shown ? "warn" : "success");
      // Работа встала и ждёт человека — сообщить в Telegram (#28); при ручном просмотре hh.ru капча — не новость
      if (shown && d.state.queueRunning)
        void d.backend.reportEvent("Captcha", location.href);
    });
    this.settings = new Settings(d.config, d.storage, d.panel, d.logger, d.backend);

    const ctx: StepContext = {
      state: d.state,
      config: d.config,
      storage: d.storage,
      panel: d.panel,
      logger: d.logger,
      limit: d.limit,
      backend: d.backend,
      captcha: this.captcha,
      nav: d.nav ?? browserNavigator,
      tab: d.tab ?? ANY_TAB,
      pipeline: this.createPipeline(),
      // Тот же поддомен, что у формы: запрос same-origin
      archive: d.archive ?? pageArchiveChecker(() => (d.nav ?? browserNavigator).url()),
      persist: () => this.persist(),
    };
    this.search = new SearchStep(ctx);
    this.queue = new QueueStep(ctx);
    this.rejects = new RejectStep(ctx);
    this.dialogs = new DialogStep(ctx);
    this.ctx = ctx;
  }

  async init(): Promise<void> {
    const { state, panel } = this.d;
    const page = pageOf(this.ctx);
    const kind = pageKindOf(page);
    panel.setPage(kind.kind, kind.title);
    panel.setSettings(panelSettings(this.d.config));
    await this.refresh();
    this.captcha.start();
    this.d.repo.watch((saved) => this.onExternalState(saved));
    watchConfig(this.d.storage, (config) => this.onExternalConfig(config));
    await this.queue.adoptOrphan();
    if (state.queueRunning && !this.queue.owns(state)) {
      panel.setQueueActive(true, true);
      panel.setStep("Очередь идёт в другой вкладке — «Остановить очередь» остановит её");
      return;
    }
    panel.setQueueActive(state.queueRunning);

    const step = pickStep([this.queue, this.dialogs], page, state);
    if (step) {
      await step.resume();
      return;
    }
    // «Синхронизировать чаты» нажали на другой странице: она открыла чаты — запускаем сбор (#123)
    if (page === "chat" && (await takeChatSync(this.d.storage))) {
      await this.dialogs.run();
      return;
    }
    panel.setStep(page === "search" ? "Отправьте страницу в поиск на сервере или запустите очередь"
      : page === "chat" ? "«Синхронизировать чаты» — чаты на сервер" : "");
  }

  // Команда из панели (#123): одна точка входа для панели на странице и боковой панели браузера
  async dispatch(command: PanelCommand): Promise<void> {
    switch (command.kind) {
      case "queue.toggle":
        return this.queue.toggle();
      case "queue.dryRun":
        return this.settings.setQueueDryRun(command.enabled);
      case "search.send":
        await this.search.send();
        return this.settings.refreshServer(true);
      case "chats.sync":
        return this.syncChats();
      case "server.check":
        return this.settings.checkServer();
      case "rejects.next":
        await this.rejects.next();
        return;
      case "rejects.auto":
        return this.rejects.toggleAuto();
      case "mode":
        return this.settings.setPanelMode(command.mode);
      case "options.open":
      case "site.open":
        // Это делает вид: у content script нет доступа к этим API
        return;
    }
  }

  // На странице чатов — сразу; с другой страницы — открыть чаты, сбор запустится после их загрузки
  private async syncChats(): Promise<void> {
    const { state, logger, storage } = this.d;
    const nav = this.ctx.nav;
    if (pageOf(this.ctx) === "chat") return this.dialogs.run();
    if (state.queueRunning) {
      logger.log("Идёт очередь: сначала остановите её, потом синхронизируйте чаты", "error");
      return;
    }
    await requestChatSync(storage);
    logger.log("Открываю страницу откликов и чатов для синхронизации", "step");
    nav.go(chatsPageUrl(new URL(nav.url()).origin));
  }

  private createPipeline(): ApplyPipeline {
    const d = this.d;
    return new ApplyPipeline({
      logger: d.logger,
      letters: d.letters,
      resumes: d.resumes,
      // Лимиты уже учтены в onSubmit — в момент нажатия
      onApplied: async (vacancy) => {
        d.state.applied++;
        d.logger.log(`Отклик зарегистрирован: "${vacancy.title}"`, "success");
      },
      // Отметка и лимиты — до клика (#52): после него hh.ru может сразу загрузить новую страницу, а «Непонятное» тоже ушло
      onSubmit: async () => {
        d.state.queueSubmitted = d.state.queueCurrent?.id ?? null;
        d.state.queueRunApplied++;
        await d.limit.increment();
        await this.persist();
      },
      captchaHit: () => this.captcha.consumeHit(),
      onChange: () => this.persist(),
      dryRun: () => isDryRun(d.state.queueRunning, d.config.queueDryRun),
      active: (vacancy) => isQueueOn(d.state.queueRunning, d.state.queueCurrent, vacancy.link),
    });
  }

  // Состояние записала другая вкладка (#51)
  private onExternalState(saved: AppState): void {
    const { state, panel } = this.d;
    if (state.queueRunning && this.queue.owns(state)) {
      // Своя очередь: из другой вкладки принимается только остановка этого же запуска
      if (!saved.queueRunning && saved.queueRunId === state.queueRunId)
        stopQueue(this.ctx, "Очередь остановлена в другой вкладке");
      return;
    }
    // Свой остановленный запуск не воскрешать: другая вкладка могла записать его до того, как узнала об остановке
    if (saved.queueRunning && saved.queueRunId === state.queueRunId && state.queueTab === this.ctx.tab.self) return;
    // Чужая очередь (или её нет): поля очереди — как в хранилище, чтобы своя запись их не затёрла
    state.queueRunning = saved.queueRunning;
    state.queueCurrent = saved.queueCurrent;
    state.queueTried = saved.queueTried;
    state.queueRunApplied = saved.queueRunApplied;
    state.queueFailStreak = saved.queueFailStreak;
    state.queueOpened = saved.queueOpened;
    state.queueSubmitted = saved.queueSubmitted;
    state.queueTab = saved.queueTab;
    state.queueRunId = saved.queueRunId;
    panel.setQueueActive(state.queueRunning, state.queueRunning);
    if (state.queueRunning) panel.setStep("Очередь идёт в другой вкладке — «Остановить очередь» остановит её");
  }

  // Настройки сохранила другая вкладка (#51): пробный режим и лимиты — сразу, в том числе для вакансии в работе
  private onExternalConfig(config: AppConfig): void {
    const { logger, panel, limit } = this.d;
    const dryRunChanged = config.queueDryRun !== this.d.config.queueDryRun;
    Object.assign(this.d.config, config);
    limit.limit = config.dailyLimit;
    panel.setSettings(panelSettings(config));
    if (dryRunChanged)
      logger.log(config.queueDryRun ? "Пробный режим включён в другой вкладке" : "Пробный режим выключен в другой вкладке", "warn");
    void this.refresh();
  }

  private async persist() {
    await this.d.repo.save(this.d.state);
    await this.refresh();
  }

  private async refresh() {
    const { state, limit, panel } = this.d;
    panel.setStats({
      daily: await limit.count(),
      dailyLimit: limit.limit,
      runApplied: state.queueRunApplied,
      runLimit: this.d.config.queueRunLimit,
    });
  }
}
