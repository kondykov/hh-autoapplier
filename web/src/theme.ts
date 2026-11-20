// Тема: системная → тёмная → светлая; выбор — в localStorage (может быть недоступен — тогда только на эту вкладку).
// До отрисовки тему ставит встроенный скрипт index.html, чтобы страница не мигала
import { Icon, type IconName } from "@hh-autoapplier/ui";

export type Theme = "system" | "dark" | "light";
const THEMES: [Theme, IconName, string][] = [["system", "theme", "Тема: как в системе"], ["dark", "moon", "Тема: тёмная"], ["light", "sun", "Тема: светлая"]];
let button: HTMLButtonElement | null = null;

export const currentTheme = (): Theme => (document.documentElement.dataset.theme as Theme | undefined) ?? "system";

function applyTheme(name: Theme): void {
  if (name === "system") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = name;
  const [, icon, title] = THEMES.find(([n]) => n === name) ?? THEMES[0];
  if (!button) return;
  button.replaceChildren(Icon(icon));
  button.title = title;
  button.setAttribute("aria-label", title);
}

export function setTheme(name: Theme): void {
  try {
    if (name === "system") localStorage.removeItem("theme");
    else localStorage.setItem("theme", name);
  } catch { /* хранилище недоступно: тема только на эту вкладку */ }
  applyTheme(name);
}

// Следующая тема по кругу
export function cycleTheme(): void {
  const now = currentTheme();
  setTheme(THEMES[(THEMES.findIndex(([n]) => n === now) + 1) % THEMES.length][0]);
}

// Подпись и значок текущей темы — для пункта меню аккаунта (#128)
export function themeLabel(): { label: string; icon: IconName } {
  const [, icon, label] = THEMES.find(([n]) => n === currentTheme()) ?? THEMES[0];
  return { label, icon };
}

// Кнопка в шапке (на странице входа; после входа тема — в меню аккаунта) перебирает темы по кругу
export function initThemeButton(node: HTMLButtonElement): void {
  button = node;
  node.onclick = cycleTheme;
  applyTheme(currentTheme());
}
