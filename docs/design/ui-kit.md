# UI kit (#129)

Общие токены, иконки и компоненты для веба backend и расширения (и будущих расширений). Витрина — `/kit` в вебе, только администратору.

## Где лежит

`packages/ui/src/` — единственная копия, пакет `@hh-autoapplier/ui` в npm workspaces (с #131; до него — `web/src/ui/`, ещё раньше — `wwwroot/ui/`):

| Файл | Что внутри |
|---|---|
| `tokens.css` | палитра обеих тем, шрифты, отступы, радиусы, тени; срабатывает на `:root` и на `:host` (shadow root) |
| `ui.css` | основа (поля, кнопки) и стили компонентов; без каркаса веба |
| `ui.ts` | ES-модуль на TypeScript: `el`, `nodes`, `safeUrl`, компоненты, `injectStyles`, `injectIcons`, `setIconSprite`; типы параметров — рядом с компонентами |
| `icons.svg` | спрайт иконок 24×24; список имён — `ICONS` в `ui.ts` (тип `IconName`) |

Каркас веба (шапка, `main`, строка статуса, витрина) — `web/src/app.css`, в kit не входит.

Веб собирается (`npm run build:web`, `web/scripts/build.mjs`): `tokens.css`, `ui.css` и `app.css` склеиваются в `app.[hash].css`, спрайт копируется как `icons.[hash].svg`, код — `app.[hash].js`; всё — в `server/src/HhAutoApplier.Api/wwwroot` (генерируется, в git его нет). Отпечаток в имени решает кэш браузера: новая сборка — новые имена, `index.html` отдаётся с `no-cache`. Esbuild расширения читает файлы kit из `web/src/ui/` по относительному пути.

## Правила kit

- Только `export`, без глобальных переменных и действий при импорте. Слушатели вешает компонент при создании (`Sheet`) и снимает `dispose()`.
- Нет состояния приложения, `fetch` и API: данные, тексты кнопок и обработчики приходят параметрами. Подписи по умолчанию — только общие слова («Отменить», «Закрыть», «Загрузка»), их можно заменить через `labels` или `label`.
- Данные — только текстом (`textContent`, текстовые дети `el`), ссылки — через `safeUrl`. `innerHTML` есть только в `injectIcons` для собственного спрайта kit.
- Каждый компонент работает и в документе, и в shadow root. Клик «мимо» листа определяется по `composedPath`, тост кладётся в переданный `root`.
- Подтверждения (#128): обратимое действие выполняется сразу, а `Toast` с `undo` 6 с даёт «Отменить» (вызывается не больше одного раза; в вебе — `undoable` из `web/src/undo.ts`). Необратимое — через `Confirm` (модальный диалог, можно с полем). `confirm()` и `prompt()` браузера не использовать.
- Действие с выбором варианта («Оценить заново через ▾», #107) — `Menu(…, { button: true })`: триггер в виде обычной кнопки, пункты — варианты.
- Шапка и палитра команд (#128): `SearchButton` — кнопка-поле «Найти вакансию, команду… Ctrl K» (класс `compact` или CSS каркаса — только значок); `CommandPalette({ groups, sources })` — модальный диалог с полем (combobox) и списком (listbox): Ctrl+K / Cmd+K, Esc, клик мимо, ↑↓ и Enter, фокус в поле и возврат фокуса. `groups` — функция (права и подписи на момент открытия), `sources` — источники с запросом после паузы ввода (поиск вакансий). Фильтр — `filterPalette`. `NavItem.alert` / `Alert` — знак «!» у раздела (warn — пауза, bad — сбой), причина — в `title` и `aria-label`.

## Веб

```ts
// web/src/main.ts: стили — импортом (сборка вынесет их в app.[hash].css), спрайт — адресом файла сборки
import "./styles.css";                       // @import @hh-autoapplier/ui/tokens.css, @hh-autoapplier/ui/ui.css, app.css
import iconsUrl from "@hh-autoapplier/ui/icons.svg"; // "/icons.[hash].svg"
import { Button, Card, setIconSprite } from "@hh-autoapplier/ui";
setIconSprite(iconsUrl);
```

Тема: `data-theme="dark|light"` на `<html>`, без атрибута — как в системе.

## Расширение (#123)

Панель расширения (боковая, плавающая, плашка) и страница настроек — на kit, без своей палитры:

- `extensions/hh/src/shared/kit.ts` — `mountKit(root, ...свои стили)`: `injectStyles` с `tokens.css` + `ui.css` и `injectIcons` со спрайтом. `root` — shadow root панели на странице сайта или `document` страницы расширения (боковая панель, настройки). Спрайт всегда встраивается: внешний файл на чужом сайте не загрузится, а в `dist` копировать нечего.
- Сборка (`extensions/hh/scripts/build.mjs`): `*.css` и `*.svg` импортируются текстом; CSS сжимается esbuild, у SVG схлопываются пробелы. Объявления модулей для `tsc` — `extensions/hh/src/assets.d.ts` (в расширении — текст, в вебе `*.svg` — адрес файла, `web/src/env.d.ts`).
- Компоненты панели — `Button`, `Segmented` (вид панели), `Stat` (сводка очереди), `Callout` (капча), `Icon` (настройки, свернуть); свои стили — только раскладка: `extensions/hh/src/shared/panel/panel.css`, `extensions/hh/src/options/options.css`. Цвета в них — только токены.
- Тема — по системе (`prefers-color-scheme`), как в вебе; вручную — `data-theme` на хосте панели или на `<html>` страницы расширения.
- Панель — интерфейс для мыши: высоты касания kit на ширине ≤720px (`--tap`) в ней сняты, иначе узкая боковая панель получала кнопки телефона.
- Граница общего кода (`extensions/hh/tests/settings.test.ts`): `src/shared` и `src/infra` импортируют только общий код и kit (`@hh-autoapplier/ui`), а сам kit не импортирует ничего (`packages/ui/tests/ui.test.ts`).

Размер `dist/content.js` (несжатый IIFE): 150 КБ → 182 КБ. Основное — текст `ui.css` (~21 КБ сжатым), спрайт (~4 КБ) и используемые функции `ui.ts`; своя палитра панели (~5,5 КБ) ушла.
