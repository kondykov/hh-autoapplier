import type { ServerLink, ServerQueue } from "../shared/panel/model";
import type { BackendClient, StatsResult } from "./client";
import { describeServer, incompatibleMessage, UNKNOWN_VERSION_MESSAGE, type ServerVersionResult } from "./version";

// Индикатор связи в шапке панели (#123): версия и контракт с /api/version, сводка очереди с /api/stats
export function describeLink(version: ServerVersionResult, stats: StatsResult): { link: ServerLink; queue: ServerQueue | null } {
  const queue = stats.ok ? { queued: stats.counts.Queued ?? 0, review: stats.counts.Review ?? 0 } : null;
  switch (version.state) {
    case "error":
      return { link: { state: "error", label: "нет связи", detail: version.error.charAt(0).toUpperCase() + version.error.slice(1) }, queue };
    case "incompatible":
      return { link: { state: "error", label: "несовместим", detail: incompatibleMessage(version.server), contract: version.server.apiVersion }, queue };
    case "unknown":
      return { link: { state: "warn", label: "версия ?", detail: UNKNOWN_VERSION_MESSAGE }, queue };
    case "compatible": {
      const { server } = version;
      if (!stats.ok)
        return {
          link: { state: "warn", label: stats.status === 401 ? "токен?" : server.version, detail: `Сервер ${describeServer(server)}; сводка очереди не получена: ${stats.error}`, contract: server.apiVersion },
          queue,
        };
      return { link: { state: "ok", label: server.version, detail: `Связь есть. Сервер ${describeServer(server)}`, contract: server.apiVersion }, queue };
    }
  }
}

// quiet — фоновая проверка: без разрешения на адрес страница настроек сама не откроется
export async function fetchLink(backend: Pick<BackendClient, "getServerVersion" | "getStats">, quiet: boolean) {
  const version = await backend.getServerVersion(quiet);
  // Сервер не ответил на версию — сводку не спрашиваем
  const stats: StatsResult = version.state === "error" ? { ok: false, status: 0, error: version.error } : await backend.getStats(quiet);
  return describeLink(version, stats);
}
