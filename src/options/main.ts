import { serverOriginPattern } from "../api/transport";
import { loadConfig } from "../core/config";
import { ChromeStorage } from "../infra/storage";

// Страница настроек расширения (#101): разрешение на адрес backend в домашней сети.
// chrome.permissions.request требует нажатия на странице расширения — из content script на hh.ru его не вызвать
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

function show(text: string, ok: boolean) {
  const status = $("status");
  status.textContent = text;
  status.className = ok ? "ok" : "warn";
}

async function render() {
  const { server } = await loadConfig(new ChromeStorage());
  $("server").textContent = server.url || "не задан";
  const button = $<HTMLButtonElement>("grant");
  const pattern = serverOriginPattern(server.url);
  if (!pattern) {
    button.disabled = true;
    show("Адрес сервера должен начинаться с http:// или https:// — исправьте его в панели на hh.ru", false);
    return;
  }

  const granted = await chrome.permissions.contains({ origins: [pattern] });
  button.disabled = granted;
  if (granted) show("Доступ разрешён. На hh.ru нажмите «Проверить связь».", true);
  else show(`Нет доступа к ${pattern.replace("/*", "")}.`, false);

  button.onclick = async () => {
    const ok = await chrome.permissions.request({ origins: [pattern] }).catch(() => false);
    if (ok) {
      button.disabled = true;
      show("Доступ разрешён. На hh.ru нажмите «Проверить связь».", true);
    } else show("Chrome не дал разрешение: попробуйте ещё раз и нажмите «Разрешить» в окне Chrome.", false);
  };
}

void render();
// Адрес поменяли в панели, пока страница открыта
chrome.storage.onChanged.addListener(() => void render());
