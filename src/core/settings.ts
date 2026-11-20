import type { BackendClient } from "../api/client";
import { fetchLink } from "../api/server-link";
import type { Logger } from "../infra/logger";
import type { KeyValueStorage } from "../infra/storage";
import type { PanelMode } from "../shared/panel/model";
import type { PanelStore } from "../shared/panel/store";
import { saveConfig, type AppConfig } from "./config";

// Настройки, которые меняются из панели: пробный режим очереди и вид панели. Остальное (сервер, лимиты, тексты) —
// на странице настроек расширения (#123); их изменения приходят через watchConfig
export class Settings {
  constructor(
    private config: AppConfig,
    private storage: KeyValueStorage,
    private panel: PanelStore,
    private logger: Logger,
    private backend: Pick<BackendClient, "getServerVersion" | "getStats">,
  ) {}

  private save = () => saveConfig(this.storage, this.config);

  // Индикатор связи и сводка очереди. quiet — фоновая проверка: без лишнего в журнале и без открытия настроек
  async refreshServer(quiet: boolean) {
    if (!quiet) this.panel.setServer({ state: "checking", label: "проверяю", detail: "Проверяю связь с сервером…" });
    const { link, queue } = await fetchLink(this.backend, quiet);
    this.panel.setServer(link, queue);
    if (!quiet) this.logger.log(link.detail, link.state === "ok" ? "success" : "warn");
  }

  checkServer() {
    return this.refreshServer(false);
  }

  async setQueueDryRun(enabled: boolean) {
    this.config.queueDryRun = enabled;
    await this.save();
    this.panel.setSettings(panelSettings(this.config));
    this.logger.log(enabled ? "Пробный режим очереди включён" : "Пробный режим очереди выключен: отклики будут отправляться", enabled ? "info" : "warn");
  }

  async setPanelMode(mode: PanelMode) {
    if (this.config.panelMode === mode) return;
    this.config.panelMode = mode;
    await this.save();
    this.panel.setSettings(panelSettings(this.config));
  }
}

export const panelSettings = (c: AppConfig) => ({
  dryRun: c.queueDryRun,
  dailyLimit: c.dailyLimit,
  runLimit: c.queueRunLimit,
  importSearch: c.importSearch,
  thanksOnReject: c.thanksOnReject,
  mode: c.panelMode,
});
