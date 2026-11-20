import { build, context, transform } from "esbuild";
import { copyFileSync, existsSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// Пути — от папки расширения (extensions/hh, #131): сборка работает из любого каталога
const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
process.chdir(root);

mkdirSync("dist", { recursive: true });
copyFileSync("manifest.json", "dist/manifest.json");
copyFileSync("src/options/options.html", "dist/options.html");
copyFileSync("src/sidepanel/sidepanel.html", "dist/sidepanel.html");

// Личное письмо лежит в gitignored letter.local.txt и вшивается только в локальную сборку: рядом с расширением
// или, как до #131, в корне репозитория
const letterFile = ["letter.local.txt", "../../letter.local.txt"].find((f) => existsSync(f));
const defaultLetter = letterFile ? readFileSync(letterFile, "utf8").trim() : "";

// Версия — из manifest.json: метка в панели показывает, какая сборка загружена. version_name — метка на время сбора релиза (0.7.0-rc.1)
const manifest = JSON.parse(readFileSync("manifest.json", "utf8"));
const version = manifest.version_name ?? manifest.version;

// UI kit (#129, packages/ui) и свои стили панели: CSS и SVG импортируются текстом (import css from "….css") —
// панель на странице сайта вставляет их в свой shadow root (injectStyles, injectIcons), страницы расширения — в документ.
// CSS сжимается, у SVG схлопываются пробелы: текст целиком попадает в content.js
const textAssets = {
  name: "text-assets",
  setup(b) {
    b.onLoad({ filter: /\.css$/ }, async (args) => {
      const { code } = await transform(readFileSync(args.path, "utf8"), { loader: "css", minify: true, target: "chrome120" });
      return { contents: code.trim(), loader: "text" };
    });
    b.onLoad({ filter: /\.svg$/ }, (args) => ({ contents: readFileSync(args.path, "utf8").replace(/<!--[\s\S]*?-->/g, "").replace(/>\s+</g, "><").trim(), loader: "text" }));
  },
};

const options = {
  // esbuild запоминает каталог запуска при импорте, до chdir: рабочий каталог — явно
  absWorkingDir: root,
  plugins: [textAssets],
  define: { __DEFAULT_LETTER__: JSON.stringify(defaultLetter), __VERSION__: JSON.stringify(version) },
  // content.js — на страницах hh.ru, background.js — service worker для запросов к backend, options.js — страница настроек (#101),
  // sidepanel.js — боковая панель браузера (#123). Общий код — src/shared: второе расширение соберётся из него своим набором входов
  entryPoints: {
    content: "src/content/main.ts",
    background: "src/background/main.ts",
    options: "src/options/main.ts",
    sidepanel: "src/sidepanel/main.ts",
  },
  outdir: "dist",
  bundle: true,
  format: "iife",
  target: "chrome120",
  sourcemap: true,
};

if (process.argv.includes("--watch")) {
  const ctx = await context(options);
  await ctx.watch();
} else {
  await build(options);
}

