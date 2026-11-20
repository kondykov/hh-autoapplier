import { chromium } from "playwright";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// Dev-браузер: Chromium с загруженным расширением, постоянным профилем и портом CDP 9222.
// Расширение — extensions/hh/dist, профиль — .profile в корне репозитория (как до #131: вход на hh.ru сохраняется)
const here = dirname(fileURLToPath(import.meta.url));
const ext = resolve(here, "../dist");
const ctx = await chromium.launchPersistentContext(resolve(here, "../../../.profile"), {
  headless: false,
  viewport: null,
  locale: "ru-RU",
  args: [
    `--disable-extensions-except=${ext}`,
    `--load-extension=${ext}`,
    "--remote-debugging-port=9222",
  ],
});

const page = ctx.pages()[0] ?? (await ctx.newPage());
page.on("console", (m) => console.log(`[console:${m.type()}] ${m.text()}`));
await page
  .goto("https://hh.ru/", { waitUntil: "domcontentloaded", timeout: 60000 })
  .catch((e) => console.log("goto:", e.message.split("\n")[0]));
console.log("Dev browser ready (CDP :9222). Закройте окно, чтобы завершить.");
await new Promise((r) => ctx.on("close", r));
