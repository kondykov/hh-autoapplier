# UI kit (#129)

Общие токены, иконки и компоненты для веба backend и расширения (и будущих расширений). Витрина — `/kit` в вебе, только администратору.

## Где лежит

`web/src/ui/` — единственная копия (с #130; до него — `wwwroot/ui/`):

| Файл | Что внутри |
|---|---|
| `tokens.css` | палитра обеих тем, шрифты, отступы, радиусы, тени; срабатывает на `:root` и на `:host` (shadow root) |
| `ui.css` | основа (поля, кнопки) и стили компонентов; без каркаса веба |
| `ui.ts` | ES-модуль на TypeScript: `el`, `nodes`, `safeUrl`, компоненты, `injectStyles`, `injectIcons`, `setIconSprite`; типы параметров — рядом с компонентами |
| `icons.svg` | спрайт иконок 24×24; список имён — `ICONS` в `ui.ts` (тип `IconName`) |

Каркас веба (шапка, `main`, строка статуса, витрина) — `web/src/app.css`, в kit не входит.

Веб собирается (`npm run build:web`, `scripts/build-web.mjs`): `tokens.css`, `ui.css` и `app.css` склеиваются в `app.[hash].css`, спрайт копируется как `icons.[hash].svg`, код — `app.[hash].js`; всё — в `server/src/HhAutoApplier.Api/wwwroot` (генерируется, в git его нет). Отпечаток в имени решает кэш браузера: новая сборка — новые имена, `index.html` отдаётся с `no-cache`. Esbuild расширения читает файлы kit из `web/src/ui/` по относительному пути.

## Правила kit

- Только `export`, без глобальных переменных и действий при импорте. Слушатели вешает компонент при создании (`Sheet`) и снимает `dispose()`.
- Нет состояния приложения, `fetch` и API: данные, тексты кнопок и обработчики приходят параметрами. Подписи по умолчанию — только общие слова («Отменить», «Закрыть», «Загрузка»), их можно заменить через `labels` или `label`.
- Данные — только текстом (`textContent`, текстовые дети `el`), ссылки — через `safeUrl`. `innerHTML` есть только в `injectIcons` для собственного спрайта kit.
- Каждый компонент работает и в документе, и в shadow root. Клик «мимо» листа определяется по `composedPath`, тост кладётся в переданный `root`.

## Веб

```ts
// web/src/main.ts: стили — импортом (сборка вынесет их в app.[hash].css), спрайт — адресом файла сборки
import "./styles.css";                       // @import ui/tokens.css, ui/ui.css, app.css
import iconsUrl from "./ui/icons.svg";       // "/icons.[hash].svg"
import { Button, Card, setIconSprite } from "./ui/ui";
setIconSprite(iconsUrl);
```

Тема: `data-theme="dark|light"` на `<html>`, без атрибута — как в системе.

## Расширение (пока не переведено, после слияния #123)

1. В `scripts/build.mjs` подключить загрузчик `text` для CSS и SVG kit:
   `loader: { ".css": "text", ".svg": "text" }` (для своих CSS расширения, если такие появятся, — отдельный путь или суффикс).
2. Импортировать файлы kit по относительному пути из `web/src/ui/`:

```ts
import { injectStyles, injectIcons, Button, Card } from "../../web/src/ui/ui";
import tokens from "../../web/src/ui/tokens.css";
import ui from "../../web/src/ui/ui.css";
import icons from "../../web/src/ui/icons.svg";

const root = host.attachShadow({ mode: "open" });
injectStyles(root, [tokens, ui]);   // стили — текстом в shadow root
injectIcons(root, icons);           // спрайт внутрь root: внешний спрайт с чужого сайта не грузится
root.append(Card({ title: "Очередь" }, Button("Старт", { kind: "primary", onClick: start })));
```

   Тема в панели — `host.dataset.theme = "dark"` (или без атрибута — по системе). В страницах расширения (боковая панель, настройки) можно так же через `injectStyles(document, …)` или положить файлы в `dist` и подключить `<link>`, а иконки — `setIconSprite(chrome.runtime.getURL("ui/icons.svg"))`.
3. Для `tsc` нужны объявления `declare module "*.css" { const text: string; export default text; }` и то же для `*.svg` (в расширении — текст, в вебе `*.svg` — адрес файла, `web/src/env.d.ts`). Типы kit — в самом `ui.ts`.
4. `THEME_VARS` из `src/shared/panel/theme.ts` удалить: палитра — только `tokens.css`.

Проверено 2026-11-18 разовой сборкой во временной папке (без записи в `dist/`): esbuild собирает `ui.js` + `tokens.css` + `ui.css` + `icons.svg` в один IIFE (~37 КБ). В Chromium внутри shadow root применяются стили обеих тем, иконки видны, тост остаётся внутри root.
