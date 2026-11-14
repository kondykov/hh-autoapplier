import type { ServerSettings } from "../core/config";

// Сообщение content script → фоновый service worker: только путь и тело, адрес и ключ фон берёт из конфига сам
export interface ApiRequest {
  type: "hh-api";
  path: string;
  method?: "GET" | "POST" | "PUT" | "DELETE";
  body?: unknown;
}

// status 0 — сервер не ответил (недоступен, таймаут, неверный адрес).
// noAccess — запроса не было: нет разрешения на адрес сервера (#101), его даёт страница настроек расширения
export interface ApiResponse {
  status: number;
  body: string;
  error?: string;
  noAccess?: boolean;
}

// Шаблон разрешения на адрес сервера для chrome.permissions: http://192.168.0.104:8080 → http://192.168.0.104/*
// (порт в шаблонах Chrome не указывается). null — адрес не http(s)
export function serverOriginPattern(url: string): string | null {
  try {
    const u = new URL(url.trim());
    return u.protocol === "http:" || u.protocol === "https:" ? `${u.protocol}//${u.hostname}/*` : null;
  } catch {
    return null;
  }
}

export const isApiRequest = (message: unknown): message is ApiRequest =>
  typeof message === "object" && message !== null && (message as ApiRequest).type === "hh-api"
  && typeof (message as ApiRequest).path === "string" && (message as ApiRequest).path.startsWith("/");

const TIMEOUT_MS = 15000;

// Выполняется в фоне. Запрос со страницы hh.ru к локальной сети Chrome блокирует (Local Network Access),
// запрос из service worker расширения с host_permissions — нет
export async function executeApiRequest(
  request: ApiRequest,
  server: ServerSettings,
  fetchFn: typeof fetch = fetch,
  hasAccess: (pattern: string) => Promise<boolean> = async () => true,
): Promise<ApiResponse> {
  const base = server.url.trim().replace(/\/+$/, "");
  if (!/^https?:\/\//.test(base)) return { status: 0, body: "", error: "адрес сервера должен начинаться с http:// или https://" };
  // Без разрешения запрос из service worker упрётся в CORS и local network access: не делаем его вовсе
  const pattern = serverOriginPattern(base);
  if (pattern && !(await hasAccess(pattern)))
    return { status: 0, body: "", noAccess: true, error: `нет разрешения на адрес ${new URL(base).host} — откройте настройки расширения и нажмите «Разрешить доступ»` };

  const headers: Record<string, string> = {};
  if (server.apiKey) headers["X-Api-Key"] = server.apiKey;
  if (request.body !== undefined) headers["Content-Type"] = "application/json";

  try {
    const response = await fetchFn(base + request.path, {
      method: request.method ?? "GET",
      headers,
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    return { status: response.status, body: await response.text() };
  } catch (e) {
    return { status: 0, body: "", error: (e as Error).message };
  }
}
