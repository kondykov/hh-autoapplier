import { describe, expect, it, vi } from "vitest";
import { BackendClient, type ApiSender } from "../src/api/client";
import { executeApiRequest, isApiRequest, serverOriginPattern, type ApiResponse } from "../src/api/transport";
import { API_VERSION } from "../src/api/version";
import { DEFAULT_SERVER, loadConfig } from "../src/core/config";
import { MemoryStorage } from "../src/infra/storage";

describe("server config", () => {
  it("дополняет сохранённый конфиг настройками сервера по умолчанию", async () => {
    const storage = new MemoryStorage();
    await storage.set("hh_autoapply_config", { dailyLimit: 10 });
    expect((await loadConfig(storage)).server).toEqual(DEFAULT_SERVER);

    await storage.set("hh_autoapply_config", { server: { apiKey: "k" } });
    expect((await loadConfig(storage)).server).toEqual({ url: DEFAULT_SERVER.url, apiKey: "k" });
  });
});

// #52: 200 с не-JSON бросал исключение вне try — очередь «включена», но стоит
describe("BackendClient: ответ 200 не JSON", () => {
  const html: ApiSender = async () => ({ status: 200, body: "<html>proxy</html>" });

  it("каждый разбор — ошибка запроса, а не исключение", async () => {
    const client = new BackendClient(html);
    expect(await client.getQueue()).toMatchObject({ ok: false, error: expect.stringContaining("не JSON") });
    expect(await client.getResumes()).toMatchObject({ ok: false });
    expect(await client.importVacancies("s", [])).toMatchObject({ ok: false });
    expect(await client.importDialogs([])).toMatchObject({ ok: false });
    expect(await client.check()).toMatchObject({ ok: false });
  });
});

describe("executeApiRequest", () => {
  const ok = (body = "{}") => vi.fn(async () => new Response(body, { status: 200 }));

  it("склеивает адрес, ставит ключ и JSON-тело", async () => {
    const fetchFn = ok("[]");
    const res = await executeApiRequest(
      { type: "hh-api", path: "/api/import", method: "POST", body: { a: 1 } },
      { url: "http://127.0.0.1:8080/", apiKey: "secret" },
      fetchFn as unknown as typeof fetch,
    );
    expect(res).toEqual({ status: 200, body: "[]" });
    const [url, init] = fetchFn.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://127.0.0.1:8080/api/import");
    expect(init.method).toBe("POST");
    expect(init.headers).toEqual({ "X-Api-Key": "secret", "Content-Type": "application/json" });
    expect(init.body).toBe('{"a":1}');
  });

  it("без ключа не шлёт заголовок, ошибку сети превращает в status 0", async () => {
    const fetchFn = vi.fn(async () => {
      throw new TypeError("Failed to fetch");
    });
    const res = await executeApiRequest({ type: "hh-api", path: "/health" }, { url: "http://x", apiKey: "" }, fetchFn);
    expect(res).toEqual({ status: 0, body: "", error: "Failed to fetch" });
  });

  it("отклоняет адрес без схемы, не делая запрос", async () => {
    const fetchFn = ok();
    const res = await executeApiRequest({ type: "hh-api", path: "/health" }, { url: "127.0.0.1:8080", apiKey: "" }, fetchFn as unknown as typeof fetch);
    expect(res.status).toBe(0);
    expect(fetchFn).not.toHaveBeenCalled();
  });

  it("адрес без разрешения — ответ noAccess без запроса (#101)", async () => {
    const fetchFn = ok();
    const hasAccess = vi.fn(async () => false);
    const res = await executeApiRequest({ type: "hh-api", path: "/health" }, { url: "http://192.168.0.104:8080", apiKey: "k" }, fetchFn as unknown as typeof fetch, hasAccess);
    expect(hasAccess).toHaveBeenCalledWith("http://192.168.0.104/*");
    expect(res).toMatchObject({ status: 0, noAccess: true });
    expect(res.error).toContain("192.168.0.104:8080");
    expect(fetchFn).not.toHaveBeenCalled();
  });

  it("адрес с разрешением — запрос как обычно", async () => {
    const fetchFn = ok();
    const res = await executeApiRequest({ type: "hh-api", path: "/health" }, { url: "http://192.168.0.104:8080", apiKey: "" }, fetchFn as unknown as typeof fetch, async () => true);
    expect(res).toEqual({ status: 200, body: "{}" });
  });

  it("шаблон разрешения — схема и хост без порта", () => {
    expect(serverOriginPattern("http://192.168.0.104:8080/")).toBe("http://192.168.0.104/*");
    expect(serverOriginPattern(" https://hh.example.test ")).toBe("https://hh.example.test/*");
    expect(serverOriginPattern("ftp://x")).toBeNull();
    expect(serverOriginPattern("127.0.0.1:8080")).toBeNull();
  });

  it("принимает только свои сообщения с относительным путём", () => {
    expect(isApiRequest({ type: "hh-api", path: "/health" })).toBe(true);
    expect(isApiRequest({ type: "hh-api", path: "http://evil/" })).toBe(false);
    expect(isApiRequest({ type: "other", path: "/health" })).toBe(false);
    expect(isApiRequest(null)).toBe(false);
  });
});

describe("BackendClient.check", () => {
  const sender = (responses: Record<string, ApiResponse>): ApiSender => async (req) => responses[req.path];

  it("сообщает о связи и размере очереди", async () => {
    const client = new BackendClient(sender({
      "/health": { status: 200, body: '{"status":"ok"}' },
      "/api/stats": { status: 200, body: '{"Queued":5,"Skipped":2}' },
      "/api/version": { status: 200, body: `{"version":"0.1.0","commit":"abc1234","apiVersion":${API_VERSION}}` },
    }));
    expect(await client.check()).toEqual({ ok: true, message: `Связь есть: вакансий на сервере 7, в очереди 5. Сервер 0.1.0 (abc1234), контракт API ${API_VERSION}` });
  });

  it("различает недоступный сервер и неверный ключ", async () => {
    const down = new BackendClient(sender({ "/health": { status: 0, body: "", error: "timeout" } }));
    expect(await down.check()).toEqual({ ok: false, message: "Сервер недоступен: timeout" });

    const badKey = new BackendClient(sender({
      "/health": { status: 200, body: "{}" },
      "/api/stats": { status: 401, body: "" },
    }));
    expect(await badKey.check()).toEqual({ ok: false, message: "Сервер доступен, но токен неверный — выпустите новый (POST /api/tokens)" });
  });

  it("переживает падение канала к фону", async () => {
    const client = new BackendClient(async () => {
      throw new Error("Extension context invalidated.");
    });
    expect(await client.check()).toEqual({ ok: false, message: "Сервер недоступен: Extension context invalidated." });
  });
});
