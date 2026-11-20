/// <reference types="vite/client" />
import { describe, expect, it } from "vitest";
import { describeLink } from "../src/api/server-link";
import { API_VERSION } from "../src/api/version";
import { DEFAULT_CONFIG, loadConfig, saveConfig } from "../src/core/config";
import { MemoryStorage } from "../src/infra/storage";
import { applyForm, maskToken, toForm } from "../src/options/form";
import { CHAT_SYNC_TTL_MS, chatsPageUrl, requestChatSync, takeChatSync } from "../src/pages/chat-sync";

describe("Конфиг: новые поля (#123)", () => {
  it("дефолты: боковая панель, «спасибо» на отказы включено", async () => {
    const config = await loadConfig(new MemoryStorage());
    expect(config.panelMode).toBe("side");
    expect(config.thanksOnReject).toBe(true);
  });

  it("старый сохранённый конфиг без новых полей получает дефолты, сохранённые значения не теряются", async () => {
    const storage = new MemoryStorage();
    await storage.set("hh_autoapply_config", { dailyLimit: 42, importSearch: "dotnet" });
    const config = await loadConfig(storage);
    expect(config).toMatchObject({ dailyLimit: 42, importSearch: "dotnet", panelMode: "side", thanksOnReject: true });
  });

  it("сохранённые новые поля читаются; неизвестный вид панели — дефолт", async () => {
    const storage = new MemoryStorage();
    await saveConfig(storage, { ...DEFAULT_CONFIG, panelMode: "pill", thanksOnReject: false });
    expect(await loadConfig(storage)).toMatchObject({ panelMode: "pill", thanksOnReject: false });

    await storage.set("hh_autoapply_config", { panelMode: "sidebar" });
    expect((await loadConfig(storage)).panelMode).toBe("side");
  });
});

describe("Страница настроек: форма", () => {
  const config = { ...DEFAULT_CONFIG, server: { url: "http://127.0.0.1:8080", apiKey: "hh_secret_token_3f9a" }, queueDryRun: false };

  it("пустой токен оставляет сохранённый, остальные поля переносятся", () => {
    const form = { ...toForm(config), url: " http://192.168.0.104:8080/ ", importSearch: " dotnet-remote ", dailyLimit: "148", queueRunLimit: "10", thanksOnReject: false, panelMode: "floating" };
    const result = applyForm(config, form);
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.server).toEqual({ url: "http://192.168.0.104:8080", apiKey: "hh_secret_token_3f9a" });
    expect(result.config).toMatchObject({ importSearch: "dotnet-remote", dailyLimit: 148, queueRunLimit: 10, thanksOnReject: false, panelMode: "floating" });
    // Пробного режима на странице нет — его значение не трогается
    expect(result.config.queueDryRun).toBe(false);
  });

  it("новый токен заменяет сохранённый", () => {
    const result = applyForm(config, { ...toForm(config), apiKey: "  hh_new  " });
    expect(result.ok && result.config.server.apiKey).toBe("hh_new");
  });

  it("неверные поля — ошибки, конфиг не меняется", () => {
    const result = applyForm(config, { ...toForm(config), url: "192.168.0.104", dailyLimit: "0", queueRunLimit: "-1", rejectionReply: "  ", letter: "", panelMode: "x" });
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(Object.keys(result.errors).sort()).toEqual(["dailyLimit", "letter", "panelMode", "queueRunLimit", "rejectionReply", "url"]);
  });

  it("лимит — только целое число", () => {
    expect(applyForm(config, { ...toForm(config), dailyLimit: "1.5" }).ok).toBe(false);
    expect(applyForm(config, { ...toForm(config), queueRunLimit: "0" }).ok).toBe(true);
  });

  it("токен в форме не подставляется, а в подсказке — маской", () => {
    expect(toForm(config).apiKey).toBe("");
    expect(maskToken("hh_secret_token_3f9a")).toBe("hh_••••3f9a");
    expect(maskToken("short")).toBe("••••");
    expect(maskToken("")).toBe("");
  });
});

describe("Индикатор связи", () => {
  const server = { version: "0.4.1", commit: "abc1234", apiVersion: API_VERSION };

  it("совместимый сервер: версия и сводка очереди", () => {
    const { link, queue } = describeLink({ state: "compatible", server }, { ok: true, counts: { Queued: 2, Review: 7, Applied: 40 } });
    expect(link).toMatchObject({ state: "ok", label: "0.4.1", contract: API_VERSION });
    expect(queue).toEqual({ queued: 2, review: 7 });
  });

  it("несовместимый контракт и недоступный сервер — ошибка", () => {
    expect(describeLink({ state: "incompatible", server: { ...server, apiVersion: API_VERSION + 1 } }, { ok: true, counts: {} }).link.state).toBe("error");
    const down = describeLink({ state: "error", error: "сервер недоступен" }, { ok: false, status: 0, error: "сервер недоступен" });
    expect(down.link).toMatchObject({ state: "error", label: "нет связи" });
    expect(down.queue).toBeNull();
  });

  it("неверный токен при живом сервере — предупреждение", () => {
    const { link } = describeLink({ state: "compatible", server }, { ok: false, status: 401, error: "неверный токен" });
    expect(link).toMatchObject({ state: "warn", label: "токен?" });
  });
});

describe("Синхронизация чатов с любой страницы", () => {
  it("просьба забирается один раз и только свежая", async () => {
    const storage = new MemoryStorage();
    await requestChatSync(storage, 1000);
    expect(await takeChatSync(storage, 2000)).toBe(true);
    expect(await takeChatSync(storage, 2000)).toBe(false);

    await requestChatSync(storage, 1000);
    expect(await takeChatSync(storage, 1000 + CHAT_SYNC_TTL_MS + 1)).toBe(false);
  });

  it("страница откликов — на том же поддомене", () => {
    expect(chatsPageUrl("https://tver.hh.ru")).toBe("https://tver.hh.ru/applicant/negotiations");
  });
});

// Общий код (#123) не тянет hh-специфичное: второе расширение должно собираться из src/shared и src/infra.
// UI kit (#129, web/src/ui) — тоже общий: один для веба и всех расширений
const SHARED = ["shared", "infra", "../web/src/ui"];
const KIT = (import.meta.glob("../web/src/ui/ui.ts", { query: "?raw", import: "default", eager: true }) as Record<string, string>)["../web/src/ui/ui.ts"];
const SOURCES = import.meta.glob("../src/{shared,infra}/**/*.ts", { query: "?raw", import: "default", eager: true }) as Record<string, string>;

// Путь относительно src: "../src/shared/panel/view.ts" + "./model" → "shared/panel/model"; выше src — с "../"
function resolveFrom(file: string, spec: string): string {
  const parts = file.replace(/^\.\.\/src\//, "").split("/").slice(0, -1);
  for (const piece of spec.split("/")) {
    if (piece === "..") parts.length && parts.at(-1) !== ".." ? parts.pop() : parts.push("..");
    else if (piece !== ".") parts.push(piece);
  }
  return parts.join("/");
}

describe("Граница общего кода", () => {
  it("исходники найдены", () => {
    expect(Object.keys(SOURCES).some((f) => f.includes("shared/panel/view.ts"))).toBe(true);
  });

  it("src/shared и src/infra импортируют только общий код", () => {
    for (const [file, text] of Object.entries(SOURCES)) {
      for (const m of text.matchAll(/from\s+"(\.[^"]+)"/g)) {
        const target = resolveFrom(file, m[1]);
        expect(SHARED.some((d) => target.startsWith(`${d}/`)), `${file} → ${target}`).toBe(true);
      }
    }
  });

  it("UI kit ничего не импортирует: в нём нет кода приложений", () => {
    expect(KIT.length).toBeGreaterThan(0);
    expect(KIT).not.toMatch(/^\s*import\s/m);
  });
});
