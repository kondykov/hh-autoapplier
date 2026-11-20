// Транспорт веба (#130): fetch с того же адреса, вход — cookie. Расширение ходит через service worker (src/api/transport.ts),
// здесь это не нужно: страница и API — один источник, CORS нет

export class Unauthorized extends Error {}

export async function api<T = unknown>(path: string, options: RequestInit = {}): Promise<T> {
  const res = await fetch(path, { ...options, headers: { "Content-Type": "application/json" } });
  const body = res.status === 204 ? null : await res.json().catch(() => null);
  if (res.status === 401) throw new Unauthorized(body?.message ?? "нужен вход");
  if (res.status === 403) throw new Error(body?.message ?? "нет прав");
  // Ошибки проверки полей (400) — списком по полям
  const fields = body?.errors ? Object.values(body.errors as Record<string, string[]>).flat().join("; ") : null;
  if (!res.ok) throw new Error(body?.message ?? fields ?? body?.title ?? `сервер ответил ${res.status}`);
  return body as T;
}

export const post = <T = unknown>(path: string, body?: unknown) =>
  api<T>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });
export const put = <T = unknown>(path: string, body: unknown) => api<T>(path, { method: "PUT", body: JSON.stringify(body) });
export const del = <T = unknown>(path: string) => api<T>(path, { method: "DELETE" });
