import { describe, expect, it } from "vitest";
import { isAppLink, legacyHashPath, parseRoute, path, renamedPath } from "../src/router";

describe("переименованные разделы (#128)", () => {
  it("новые пути — свои разделы", () => {
    expect(parseRoute("/candidate").name).toBe("candidate");
    expect(parseRoute("/system").name).toBe("system");
    expect(parseRoute("/overview").name).toBe("overview");
  });

  it.each([
    ["/resumes", "", "/candidate"],
    ["/admin", "?tab=users", "/system?tab=users"],
    ["/resumes/x", "", "/candidate/x"],
  ])("%s%s → %s", (p, q, target) => expect(renamedPath(p, q)).toBe(target));

  it("остальные пути не переадресуются", () => {
    expect(renamedPath("/review")).toBeNull();
    expect(renamedPath("/")).toBeNull();
    expect(renamedPath("/administrator")).toBeNull();
  });

  it.each([["#resumes", "/candidate"], ["#admin", "/system"], ["#account", "/account"], ["#overview", "/overview"]])(
    "старый якорь %s → %s", (hash, target) => expect(legacyHashPath(hash)).toBe(target));
});

describe("parseRoute", () => {
  it("раздел, подраздел и параметры из адреса", () => {
    const r = parseRoute("/vacancies", "?status=Queued");
    expect(r.name).toBe("vacancies");
    expect(r.query.get("status")).toBe("Queued");
    expect(parseRoute("/kit/frame").sub).toBe("frame");
  });

  it("«/» и неизвестный путь — дашборд", () => {
    expect(parseRoute("/").name).toBe("dashboard");
    expect(parseRoute("/nope/x").name).toBe("dashboard");
  });
});

describe("path", () => {
  it("пустые параметры пропускаются, подраздел — вторым сегментом", () => {
    expect(path("vacancies", { status: "Queued", search: "" })).toBe("/vacancies?status=Queued");
    expect(path("review")).toBe("/review");
    expect(path("kit", {}, "frame")).toBe("/kit/frame");
  });
});

describe("legacyHashPath: старые ссылки на якорях", () => {
  it.each([
    ["#review", "/review"],
    ["#vacancies/Queued", "/vacancies?status=Queued"],
    ["#dialogs/all", "/dialogs?filter=all"],
    ["#kit/frame", "/kit/frame"],
    ["#dashboard", "/dashboard"],
    ["#что-то", "/dashboard"],
  ])("%s → %s", (hash, target) => expect(legacyHashPath(hash)).toBe(target));

  it("без якоря переходить некуда", () => {
    expect(legacyHashPath("")).toBeNull();
    expect(legacyHashPath("#")).toBeNull();
  });
});

describe("isAppLink: какие клики веб обрабатывает сам", () => {
  const origin = "http://127.0.0.1:8094";
  const click = (href: string, extra: Partial<Parameters<typeof isAppLink>[0]> = {}) =>
    isAppLink({ href, button: 0, modified: false, ...extra }, origin);

  it("разделы того же сайта", () => {
    expect(click(`${origin}/review`)).toBe(true);
    expect(click("/vacancies?status=Failed")).toBe(true);
  });

  it("API, файлы, чужие сайты, новая вкладка и Ctrl+клик — браузеру", () => {
    expect(click("/api/report?format=csv")).toBe(false);
    expect(click("/health")).toBe(false);
    expect(click("/scalar/v1")).toBe(false);
    expect(click("/review.html")).toBe(false);
    expect(click("https://hh.ru/vacancy/1")).toBe(false);
    expect(click("/review", { target: "_blank" })).toBe(false);
    expect(click("/review", { modified: true })).toBe(false);
    expect(click("/review", { button: 1 })).toBe(false);
    expect(click("/review", { download: true })).toBe(false);
  });
});
