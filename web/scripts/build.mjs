// Сборка веба (#130): web/src → server/src/HhAutoApplier.Api/wwwroot (каталог целиком сгенерирован, в git его нет).
// Имена файлов — с отпечатком содержимого (app.[hash].js, app.[hash].css, icons.[hash].svg): новая сборка — новые имена,
// и браузер берёт свежие файлы без сброса кэша. index.html — из шаблона web/index.html со ссылками на них; его backend
// отдаёт с «no-cache», файлы с отпечатком — с кэшем на год (WebInterface.cs). web/static — как есть (старые адреса).
// node scripts/build-web.mjs [--out <каталог>]
import { build } from "esbuild";
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { basename, join, relative, resolve } from "node:path";

const outArg = process.argv.indexOf("--out");
const out = resolve(outArg > 0 ? process.argv[outArg + 1] : "server/src/HhAutoApplier.Api/wwwroot");

// Каталог — только результат сборки: старые файлы с отпечатком не копятся
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });

const result = await build({
  entryPoints: { app: "web/src/main.ts" },
  outdir: out,
  entryNames: "[name].[hash]",
  assetNames: "[name].[hash]",
  // Абсолютные адреса: страница открывается на любом пути (/vacancies, /kit/frame)
  publicPath: "/",
  loader: { ".svg": "file" },
  bundle: true,
  format: "esm",
  target: "es2022",
  minify: true,
  sourcemap: true,
  legalComments: "none",
  metafile: true,
  logLevel: "warning",
});

// Адрес файла сборки от корня сайта
const url = (file) => `/${relative(out, resolve(file)).replaceAll("\\", "/")}`;
const [jsFile, js] = Object.entries(result.metafile.outputs).find(([file, o]) => o.entryPoint && file.endsWith(".js")) ?? [];
if (!jsFile || !js.cssBundle) throw new Error("сборка веба: нет app.[hash].js или app.[hash].css");

const html = readFileSync("web/index.html", "utf8").replace("{{css}}", url(js.cssBundle)).replace("{{js}}", url(jsFile));
if (html.includes("{{")) throw new Error("web/index.html: осталась неподставленная метка");
writeFileSync(join(out, "index.html"), html);
if (existsSync("web/static")) cpSync("web/static", out, { recursive: true });

console.log(`веб собран в ${relative(process.cwd(), out)}: index.html, ${basename(jsFile)}, ${basename(js.cssBundle)}`);
