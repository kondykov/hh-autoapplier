import { afterEach, expect, it, vi } from "vitest";
import { api, apiChanges, del, post, put } from "../src/api/http";

afterEach(() => vi.unstubAllGlobals());

it("emits an event only for successful writes", async () => {
  const paths: string[] = [];
  const record = (event: Event) => paths.push((event as CustomEvent<string>).detail);
  apiChanges.addEventListener("write", record);
  const fetchMock = vi.fn()
    .mockResolvedValueOnce(new Response("[]"))
    .mockResolvedValueOnce(new Response(null, { status: 204 }))
    .mockResolvedValueOnce(new Response('{"message":"conflict"}', { status: 409 }))
    .mockResolvedValueOnce(new Response("{}"))
    .mockResolvedValueOnce(new Response(null, { status: 204 }));
  vi.stubGlobal("fetch", fetchMock);
  try {
    await api("/api/stats");
    await post("/api/vacancies/1/review", { approve: true });
    await expect(post("/api/vacancies/2/review")).rejects.toThrow("conflict");
    await put("/api/profile/llm", { evaluation: "default" });
    await del("/api/dialogs/1/external-links");
    expect(paths).toEqual(["/api/vacancies/1/review", "/api/profile/llm", "/api/dialogs/1/external-links"]);
  } finally {
    apiChanges.removeEventListener("write", record);
  }
});
