// Состояние веба и общие действия разделов: кто вошёл, чьи данные смотрим, строка статуса, перерисовка.
// Администратор может смотреть данные другого пользователя (?userId=) — только чтение: правки API делает от своего имени
import { Unauthorized } from "./api/http";
import type { Me } from "./api/types";

export const session: { me: Me | null; viewedUser: string | null } = { me: null, viewedUser: null };

// Номер последней отрисовки: раздел, загрузившийся позже следующего перехода, не перерисовывает страницу
export const page = { renders: 0 };

// Каркас (shell.ts) подставляет свои: разделы зовут перерисовку, вход и переход, не завися от каркаса
export const app = {
  render: (): Promise<void> => Promise.resolve(),
  showLogin: (): void => {},
  navigate: (_url: string, _options?: { replace?: boolean; render?: boolean }): void => {},
  loadUsers: (): Promise<void> => Promise.resolve(),
};

export const me = (): Me => {
  if (!session.me) throw new Unauthorized("нужен вход");
  return session.me;
};
export const readOnly = () => session.viewedUser !== null;

export function setStatus(text: string, error = false): void {
  const status = document.getElementById("status");
  if (!status) return;
  status.textContent = text;
  status.className = error ? "error" : "";
}

// Чтение с учётом выбранного пользователя
export function scoped(path: string): string {
  if (!session.viewedUser) return path;
  return `${path}${path.includes("?") ? "&" : "?"}userId=${encodeURIComponent(session.viewedUser)}`;
}

// Действие: итог — в строке статуса, нужен вход — форма входа. Кнопку на время запроса держит занятой Button или busy()
export async function act<T>(action: () => Promise<T>, done?: string | ((result: T) => string)): Promise<T | undefined> {
  try {
    const result = await action();
    if (done) setStatus(typeof done === "function" ? done(result) : done);
    return result;
  } catch (e) {
    if (e instanceof Unauthorized) app.showLogin();
    setStatus((e as Error).message, true);
    return undefined;
  }
}
