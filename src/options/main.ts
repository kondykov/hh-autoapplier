import { BackendClient, runtimeSender } from "../api/client";
import { fetchLink } from "../api/server-link";
import { serverOriginPattern } from "../api/transport";
import { API_VERSION } from "../api/version";
import { loadConfig, saveConfig, type AppConfig } from "../core/config";
import { ChromeStorage } from "../infra/storage";
import { VERSION } from "../shared/build-info";
import { applyForm, maskToken, toForm, type FormField, type SettingsForm } from "./form";

// Страница настроек расширения (#101, #123): сервер и токен, поиск для выдачи, лимиты, тексты, вид панели;
// разрешение на адрес backend в домашней сети — chrome.permissions.request требует нажатия на странице расширения
const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const input = (id: FormField) => $<HTMLInputElement>(id);

const storage = new ChromeStorage();
const backend = new BackendClient(runtimeSender);

function show(id: string, text: string, kind: "ok" | "warn" | "err" | "" = "") {
  const el = $(id);
  el.textContent = text;
  el.className = `status ${kind}`;
}

function fill(config: AppConfig) {
  const form = toForm(config);
  for (const key of ["url", "importSearch", "dailyLimit", "queueRunLimit", "rejectionReply", "letter", "panelMode"] as const)
    input(key).value = form[key];
  input("thanksOnReject").checked = form.thanksOnReject;
  input("apiKey").value = "";
  input("apiKey").placeholder = config.server.apiKey ? `сохранён: ${maskToken(config.server.apiKey)} — введите новый, чтобы заменить` : "не задан";
}

function read(): SettingsForm {
  return {
    url: input("url").value,
    apiKey: input("apiKey").value,
    importSearch: input("importSearch").value,
    dailyLimit: input("dailyLimit").value,
    queueRunLimit: input("queueRunLimit").value,
    rejectionReply: input("rejectionReply").value,
    thanksOnReject: input("thanksOnReject").checked,
    letter: input("letter").value,
    panelMode: input("panelMode").value,
  };
}

function showErrors(errors: Partial<Record<FormField, string>>) {
  for (const el of Array.from(document.querySelectorAll<HTMLElement>("[data-error]")))
    el.textContent = errors[el.dataset.error as FormField] ?? "";
}

// Разрешение на адрес сервера (#101)
async function renderAccess(url: string) {
  const button = $<HTMLButtonElement>("grant");
  const pattern = serverOriginPattern(url);
  if (!pattern) {
    button.disabled = true;
    show("access", "Сначала укажите адрес, начинающийся с http:// или https://", "warn");
    return;
  }
  const granted = await chrome.permissions.contains({ origins: [pattern] });
  button.disabled = granted;
  if (granted) show("access", `Доступ к ${pattern.replace("/*", "")} разрешён.`, "ok");
  else show("access", `Нет доступа к ${pattern.replace("/*", "")} — нажмите «Разрешить доступ».`, "warn");
  button.onclick = async () => {
    const ok = await chrome.permissions.request({ origins: [pattern] }).catch(() => false);
    if (ok) await renderAccess(url);
    else show("access", "Chrome не дал разрешение: попробуйте ещё раз и нажмите «Разрешить» в окне Chrome.", "err");
  };
}

async function checkLink() {
  show("link", "проверяю…");
  const { link } = await fetchLink(backend, true);
  show("link", link.state === "ok" ? `на связи, ${link.label}` : link.detail, link.state === "ok" ? "ok" : link.state === "warn" ? "warn" : "err");
}

async function init() {
  $("meta").textContent = `расширение ${VERSION} · контракт API ${API_VERSION}`;
  const config = await loadConfig(storage);
  fill(config);
  await renderAccess(config.server.url);
  void checkLink();

  $<HTMLFormElement>("form").onsubmit = async (event) => {
    event.preventDefault();
    // Поверх свежего конфига: пробный режим могли переключить в панели, пока страница открыта
    const result = applyForm(await loadConfig(storage), read());
    if (!result.ok) {
      showErrors(result.errors);
      show("status", "Не сохранено — исправьте поля", "err");
      return;
    }
    showErrors({});
    await saveConfig(storage, result.config);
    fill(result.config);
    show("status", "Сохранено", "ok");
    await renderAccess(result.config.server.url);
    void checkLink();
  };
  $("check").onclick = () => void checkLink();
  input("url").oninput = () => void renderAccess(input("url").value.trim());
}

void init();
