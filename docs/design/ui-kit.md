# UI kit (#129)

Общие токены, иконки и компоненты для веба backend и расширения (и будущих расширений). Витрина — `/#kit` в вебе, только администратору.

## Где лежит

`server/src/HhAutoApplier.Api/wwwroot/ui/` — единственная копия:

| Файл | Что внутри |
|---|---|
| `tokens.css` | палитра обеих тем, шрифты, отступы, радиусы, тени; срабатывает на `:root` и на `:host` (shadow root) |
| `ui.css` | основа (поля, кнопки) и стили компонентов; без каркаса веба |
| `ui.js` | ES-модуль: `el`, `safeUrl`, компоненты, `injectStyles`, `injectIcons`, `setIconSprite` |
| `icons.svg` | спрайт иконок 24×24; список имён — `ICONS` в `ui.js` |

Каркас веба (шапка, `main`, строка статуса, витрина) — `wwwroot/app.css`, в kit не входит.

Почему не в корне `ui/` с копированием через csproj: веб отдаёт `wwwroot` как есть, без сборки, а esbuild расширения читает файлы по любому пути. Одна копия в `wwwroot/ui` обходится без правил копирования и без расхождения между «исходником» и тем, что отдаёт сервер.

## Правила kit

- Только `export`, без глобальных переменных и действий при импорте. Слушатели вешает компонент при создании (`Sheet`) и снимает `dispose()`.
- Нет состояния приложения, `fetch` и API: данные, тексты кнопок и обработчики приходят параметрами. Подписи по умолчанию — только общие слова («Отменить», «Закрыть», «Загрузка»), их можно заменить через `labels` или `label`.
- Данные — только текстом (`textContent`, текстовые дети `el`), ссылки — через `safeUrl`. `innerHTML` есть только в `injectIcons` для собственного спрайта kit.
- Каждый компонент работает и в документе, и в shadow root. Клик «мимо» листа определяется по `composedPath`, тост кладётся в переданный `root`.

## Веб

```html
<link rel="stylesheet" href="ui/tokens.css">
<link rel="stylesheet" href="ui/ui.css">
<script type="module" src="app.js"></script>   <!-- import { Button, Card } from "./ui/ui.js" -->
```

Тема: `data-theme="dark|light"` на `<html>`, без атрибута — как в системе.

## Расширение (пока не переведено, после слияния #123)

1. В `scripts/build.mjs` подключить загрузчик `text` для CSS и SVG kit:
   `loader: { ".css": "text", ".svg": "text" }` (для своих CSS расширения, если такие появятся, — отдельный путь или суффикс).
2. Импортировать файлы kit по относительному пути из `server/src/HhAutoApplier.Api/wwwroot/ui/`:

```ts
import { injectStyles, injectIcons, Button, Card } from "../../server/src/HhAutoApplier.Api/wwwroot/ui/ui.js";
import tokens from "../../server/src/HhAutoApplier.Api/wwwroot/ui/tokens.css";
import ui from "../../server/src/HhAutoApplier.Api/wwwroot/ui/ui.css";
import icons from "../../server/src/HhAutoApplier.Api/wwwroot/ui/icons.svg";

const root = host.attachShadow({ mode: "open" });
injectStyles(root, [tokens, ui]);   // стили — текстом в shadow root
injectIcons(root, icons);           // спрайт внутрь root: внешний спрайт с чужого сайта не грузится
root.append(Card({ title: "Очередь" }, Button("Старт", { kind: "primary", onClick: start })));
```

   Тема в панели — `host.dataset.theme = "dark"` (или без атрибута — по системе). В страницах расширения (боковая панель, настройки) можно так же через `injectStyles(document, …)` или положить файлы в `dist` и подключить `<link>`, а иконки — `setIconSprite(chrome.runtime.getURL("ui/icons.svg"))`.
3. Для `tsc` нужны объявления `declare module "*.css" { const text: string; export default text; }` и то же для `*.svg`. Типы самого `ui.js` — из JSDoc или отдельного `ui.d.ts` (по мере надобности).
4. `THEME_VARS` из `src/shared/panel/theme.ts` удалить: палитра — только `tokens.css`.

Проверено 2026-11-18 разовой сборкой во временной папке (без записи в `dist/`): esbuild собирает `ui.js` + `tokens.css` + `ui.css` + `icons.svg` в один IIFE (~37 КБ). В Chromium внутри shadow root применяются стили обеих тем, иконки видны, тост остаётся внутри root.
