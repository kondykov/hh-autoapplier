// UI kit (#129, на TS — #130): компоненты-функции поверх el() — ES-модуль без фреймворков и побочных эффектов при импорте.
// Общий для веба (сборка web/, scripts/build-web.mjs) и расширения (импорт через esbuild): нет состояния приложения, fetch и API —
// данные, тексты и обработчики приходят параметрами. Работает в обычном документе и в shadow root.
// Стили — tokens.css и ui.css рядом (в вебе — в общем app.[hash].css, в shadow root — injectStyles), иконки — спрайт icons.svg.
// Безопасность: данные попадают в узлы только текстом (textContent / текстовые дети el), ссылки — через safeUrl

// Ребёнок el: строки и числа становятся текстом, null/false/undefined пропускаются, массивы разворачиваются
export type Child = Node | string | number | null | undefined | false | Child[];
// Свойства узла: data-*, aria-*, for, role — атрибутами, остальное — свойствами узла (className, value, onclick…)
export type Props = Record<string, unknown>;

// Узел из тега, свойств и детей
export function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Props = {}, ...children: Child[]): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === undefined) continue;
    if (key.startsWith("data-") || key.startsWith("aria-") || key === "for" || key === "role") node.setAttribute(key, String(value));
    else (node as unknown as Record<string, unknown>)[key] = value;
  }
  append(node, children);
  return node;
}

function append(node: Node & ParentNode, children: Child[]): void {
  node.append(...nodes(...children));
}

// Дети в плоский список для append / replaceChildren: как у el — пустые пропускаются, числа становятся текстом
export function nodes(...children: Child[]): (Node | string)[] {
  const out: (Node | string)[] = [];
  const walk = (list: Child[]) => {
    for (const child of list) {
      if (Array.isArray(child)) walk(child);
      else if (child != null && child !== false) out.push(typeof child === "number" ? String(child) : child);
    }
  };
  walk(children);
  return out;
}

// Ссылка — только http(s): javascript: и прочее из данных не попадает в href
export function safeUrl(url: string | null | undefined): string {
  try {
    const u = new URL(url ?? "");
    return u.protocol === "https:" || u.protocol === "http:" ? u.href : "#";
  } catch {
    return "#";
  }
}

export const cls = (...names: (string | false | null | undefined)[]): string => names.filter(Boolean).join(" ");
export const pct = (a: number, b: number): number => (b ? Math.round((a / b) * 100) : 0);

// ── Стили и иконки в shadow root ─────────────────────────────────────
// Веб подключает стили kit сборкой (app.[hash].css). Shadow root и страницы расширения — текстом (esbuild loader "text"):
// injectStyles(root, [tokensCss, uiCss]). Повторный вызов с тем же root ничего не дублирует
export function injectStyles(root: Document | ShadowRoot, cssTexts: string | string[]): HTMLStyleElement {
  const target: ParentNode = root instanceof Document ? root.head : root;
  let style = target.querySelector<HTMLStyleElement>("style[data-ui-kit]");
  if (!style) target.prepend(style = el("style", { "data-ui-kit": "" }));
  style.textContent = ([] as string[]).concat(cssTexts).join("\n");
  return style;
}

// Иконки: по умолчанию — внешний спрайт (веб: setIconSprite(адрес icons.[hash].svg), страницы расширения:
// setIconSprite(chrome.runtime.getURL(...))). В shadow root на чужом сайте внешний спрайт другого источника не грузится —
// тогда injectIcons(root, svgText) встраивает спрайт в root, и Icon ссылается на #имя внутри него
export const ICONS = ["dashboard", "review", "dialogs", "vacancies", "overview", "llm", "search", "resume", "user", "admin", "link", "external",
  "clock", "check", "x", "undo", "more", "plus", "copy", "alert", "hash", "theme", "moon", "sun", "logout", "kit", "chevron", "download", "lock", "key", "send"] as const;
export type IconName = (typeof ICONS)[number];
let sprite = "/ui/icons.svg";
export function setIconSprite(url: string): void { sprite = url; }
export function injectIcons(root: Document | ShadowRoot, svgText: string): void {
  const target: ParentNode = root instanceof Document ? root.body : root;
  target.querySelector("[data-ui-icons]")?.remove();
  const holder = el("div", { "data-ui-icons": "", hidden: true, "aria-hidden": "true" });
  // Текст спрайта — свой файл kit, не данные
  holder.innerHTML = svgText;
  target.append(holder);
  sprite = "";
}

// Иконка из спрайта; title — подпись для чтения с экрана, без неё иконка декоративная
export function Icon(name: IconName, { title, size }: { title?: string; size?: number } = {}): SVGSVGElement {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("class", "ic");
  svg.setAttribute("viewBox", "0 0 24 24");
  if (size) svg.style.width = svg.style.height = `${size}px`;
  if (title) {
    svg.setAttribute("role", "img");
    svg.setAttribute("aria-label", title);
  } else svg.setAttribute("aria-hidden", "true");
  const use = document.createElementNS(ns, "use");
  use.setAttribute("href", `${sprite}#${name}`);
  svg.append(use);
  return svg;
}

// ── Button ────────────────────────────────────────────────────────────
type Disableable = HTMLElement & { disabled: boolean };

// Занятость: на время действия узел выключен и помечен aria-busy, после — включён (даже если действие упало)
export async function busy<T>(node: Disableable, action: () => T | Promise<T>): Promise<T> {
  node.disabled = true;
  node.setAttribute("aria-busy", "true");
  try {
    return await action();
  } finally {
    node.disabled = false;
    node.removeAttribute("aria-busy");
  }
}

export type ButtonKind = "primary" | "ok" | "danger" | "ghost";
export interface ButtonOptions {
  kind?: ButtonKind | null;
  size?: "sm" | "lg";
  icon?: IconName;
  iconOnly?: boolean;
  type?: "button" | "submit";
  title?: string;
  disabled?: boolean;
  // Вернувший промис держит кнопку занятой до его завершения
  onClick?: (e: MouseEvent) => unknown;
  className?: string;
}

// kind: primary | ok | danger | ghost; size: sm | lg; icon — имя иконки слева (iconOnly — только иконка, text уходит в подпись)
export function Button(text: string, { kind, size, icon, iconOnly, type = "button", title, disabled, onClick, className }: ButtonOptions = {}): HTMLButtonElement {
  const b = el("button", {
    type, title: title ?? (iconOnly ? text : undefined), disabled: !!disabled,
    className: cls(kind, size, iconOnly && "icon", icon && !iconOnly && "with-icon", className) || undefined,
    "aria-label": iconOnly ? text : undefined,
  }, icon ? Icon(icon) : null, iconOnly ? null : text);
  if (onClick) b.onclick = (e) => {
    const result = onClick(e);
    if (result && typeof (result as Promise<unknown>).then === "function") return busy(b, () => result);
    return result;
  };
  return b;
}

// Ряды кнопок: Actions — под карточкой, Toolbar — фильтры и формы в строку. flush — без внешних отступов, after — отступ сверху
export const Actions = (children: Child[], { flush }: { flush?: boolean } = {}) => el("div", { className: cls("actions", flush && "flush") }, children);
export const Toolbar = (children: Child[], { flush, after }: { flush?: boolean; after?: boolean } = {}) =>
  el("div", { className: cls("bar", flush && "flush", after && "after") }, children);
// Итог действия рядом с кнопками (текст меняет вызывающий)
export const Result = (text = "") => el("span", { className: "result", textContent: text });

// ── Pill, Badge, Count ────────────────────────────────────────────────
export type Tone = "ok" | "bad" | "accent";
// Pill — статус моноширинной меткой; tone: ok | bad | accent; dot — цвет точки (например, источник)
export function Pill(text: string, { tone, dot, title }: { tone?: Tone; dot?: string; title?: string } = {}): HTMLSpanElement {
  // Метка одной строкой: длинная (имя модели) обрезается многоточием по ширине — полный текст в подсказке
  return el("span", { className: cls("pill", dot && "src", tone), title: title ?? text },
    dot ? el("i", { style: `background:${dot}` }) : null, text);
}
// Badge — залитая метка; tone: ok | accent | bad
export const Badge = (text: string, { tone }: { tone?: Tone } = {}) => el("span", { className: cls("kind", tone), textContent: text });
// Count — счётчик; 0 — ничего
export const Count = (n: number | null | undefined) => (n ? el("span", { className: "count", textContent: n > 99 ? "99+" : String(n) }) : null);

// ── Card и тексты ─────────────────────────────────────────────────────
export type MetaParts = string | (string | number | null | undefined | false)[] | null | undefined;

// Строка «серым»: части через « · », пустые выкидываются; lead — узел в начале строки (метка)
export function Meta(parts: MetaParts, { lead }: { lead?: Node } = {}): HTMLDivElement | null {
  if (parts == null) return null;
  const text = Array.isArray(parts) ? parts.filter(Boolean).join(" · ") : String(parts);
  return el("div", { className: "meta" }, lead ?? null, text);
}

export interface CardOptions {
  as?: "article" | "form" | "div";
  title?: string | null;
  href?: string | null;
  head?: Node | null;
  meta?: MetaParts;
  actions?: Child[] | null;
  decided?: boolean;
  tight?: boolean;
  className?: string;
  [prop: string]: unknown;
}

// as: article | form | div; title — заголовок h2 (href — ссылкой в новой вкладке) или head — свой узел шапки;
// meta — строка или список частей; actions — ряд кнопок внизу; decided — «решено» (приглушено); tight — плотная
export function Card({ as = "div", title, href, head, meta, actions, decided, tight, className, ...props }: CardOptions = {}, ...children: Child[]): HTMLElement {
  const heading = head ?? (title == null ? null
    : el("h2", {}, href ? el("a", { href: safeUrl(href), target: "_blank", rel: "noopener", textContent: title }) : title));
  return el(as, { className: cls("card", tight && "tight", decided && "decided", className), ...props },
    heading, Meta(meta), children, actions ? Actions(actions) : null);
}
Card.decide = (node: Element) => node.closest(".card")?.classList.add("decided");

// Заголовок страницы: h1 и подзаголовок; aside — справа (переключатели)
export function PageHead(title: string, sub?: string | null, aside?: Node): Child {
  if (!aside) return [el("h1", { textContent: title }), sub ? el("p", { className: "sub", textContent: sub }) : null];
  return el("div", { className: "page-head" },
    el("div", {}, el("h1", { textContent: title }), sub ? el("div", { className: "muted", textContent: sub }) : null), aside);
}
// Подпись блока внутри карточки
export const Caption = (text: string) => el("h3", { textContent: text });

// Callout — вывод на жёлтом, Quote — цитата сообщения, Pre — длинный текст с переносами (scroll — с прокруткой),
// Secret — токен или код, Note — служебная пометка, Disclosure — раскрывающийся блок
export const Callout = (text: string) => el("div", { className: "callout", textContent: text });
export const Quote = (text: string) => el("div", { className: "quote", textContent: text });
export const Pre = (text: string, { scroll }: { scroll?: boolean } = {}) => el("div", { className: cls("pre", scroll && "scroll"), textContent: text });
export const Secret = (text: string) => el("span", { className: "secret", textContent: text });
export const Note = (text: string) => el("div", { className: "note", textContent: text });
export const Disclosure = (summary: string, children: Child, { open }: { open?: boolean } = {}) =>
  el("details", { open: !!open }, el("summary", { textContent: summary }), children);

// ── EmptyState, Skeleton ──────────────────────────────────────────────
export const EmptyState = (text: string, { icon }: { icon?: IconName } = {}) => el("p", { className: "empty" }, icon ? Icon(icon) : null, text);
// lines — строки текста, block — плашка (карточка или график); label — подпись для чтения с экрана
export const Skeleton = ({ lines = 3, block = false, label = "Загрузка" }: { lines?: number; block?: boolean; label?: string } = {}) =>
  el("div", { className: "skel", "aria-busy": "true", "aria-label": label },
    block ? el("i", { className: "block" }) : null, Array.from({ length: lines }, () => el("i")));

// ── Tabs, TabBar, Segmented, Chips ────────────────────────────────────
export interface NavItem {
  href: string;
  label: string;
  icon?: IconName;
  active?: boolean;
  count?: number;
  // Знак «!» у раздела (#128): что-то требует внимания; title — что именно (подсказка и подпись для чтения с экрана)
  alert?: NavAlert | null;
}
export interface NavAlert {
  tone: "warn" | "bad";
  title: string;
}

// Alert — знак «!» у раздела: тон warn (пауза) или bad (сбой); текст причины — в title и aria-label
export const Alert = (a: NavAlert | null | undefined) => a
  ? el("span", { className: cls("alert", a.tone), title: a.title, role: "img", "aria-label": `Требует внимания: ${a.title}`, textContent: "!" })
  : null;

// Tabs — ссылки разделов; со значком подпись — в .tab-label: шапка узкого компьютера может оставить только значок
export const Tabs = (items: NavItem[], { label }: { label?: string } = {}) => el("div", { className: "tabs", role: label ? "group" : undefined, "aria-label": label },
  items.map((t) => el("a", { href: t.href, className: t.active ? "active" : "", "aria-current": t.active ? "page" : undefined, title: t.icon ? t.label : undefined },
    t.icon ? Icon(t.icon) : null, t.icon ? el("span", { className: "tab-label", textContent: t.label }) : t.label, Count(t.count), Alert(t.alert))));

// TabBar — пункты нижней панели телефона и, последним, свой узел (кнопка «Ещё»)
export const TabBar = (items: NavItem[], more?: Node | null): Node[] => [
  ...items.map((t) => el("a", { href: t.href, className: t.active ? "active" : "", "aria-current": t.active ? "page" : undefined },
    t.icon ? Icon(t.icon) : null, t.label, Count(t.count), Alert(t.alert))),
  ...(more ? [more] : []),
];

export interface ChoiceOption<T> {
  value: T;
  label: string;
  color?: string;
  disabled?: boolean;
  title?: string;
}

// Выбор одного из кнопок: подсветка и aria-pressed переключаются на месте, onChange(value) — дальше решает вызывающий
function choice<T>(options: ChoiceOption<T>[], value: T, onChange: (value: T) => void, make: (o: ChoiceOption<T>) => HTMLButtonElement) {
  const buttons: HTMLButtonElement[] = options.map((o) => {
    const b = make(o);
    b.type = "button";
    b.className = o.value === value ? "on" : "";
    b.setAttribute("aria-pressed", String(o.value === value));
    b.onclick = () => {
      buttons.forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", String(x === b)); });
      onChange(o.value);
    };
    return b;
  });
  return buttons;
}

// Segmented — один из нескольких
export const Segmented = <T>(options: ChoiceOption<T>[], value: T, onChange: (value: T) => void, { label }: { label?: string } = {}) =>
  el("div", { className: "seg", role: "group", "aria-label": label },
    choice(options, value, onChange, (o) => el("button", { textContent: o.label })));

// Chips — фильтр кнопками с цветной точкой; label — подпись слева
export const Chips = <T>(options: ChoiceOption<T>[], value: T, onChange: (value: T) => void, { label }: { label?: string } = {}) =>
  el("div", { className: "chips", role: "group", "aria-label": label },
    label ? el("span", { className: "muted", textContent: label }) : null,
    choice(options, value, onChange, (o) => el("button", { disabled: !!o.disabled, title: o.title ?? "" }, o.color ? el("i", { style: `background:${o.color}` }) : null, o.label)));

// ── Stat, Meter, Funnel, Bars, RankList ───────────────────────────────
export interface StatOptions {
  value: number | string;
  unit?: string;
  label: string;
  note?: string | null;
  trend?: "up" | "down" | null;
  href?: string;
}

// Stat — KPI: value (+ unit мелко), label, note — строка ниже; trend: up | down — цвет строки. href — компактная плитка-ссылка
export function Stat({ value, unit, label, note, trend, href }: StatOptions): HTMLElement {
  if (href) return el("a", { className: "tile", href }, el("b", { textContent: String(value) }), el("span", { textContent: label }));
  return el("div", { className: "card kpi" },
    el("b", {}, String(value), unit ? el("small", { textContent: unit }) : null),
    el("span", { textContent: label }),
    note != null ? el("i", { className: trend ?? "", textContent: note }) : null);
}
export const Stats = (children: Child[], { tiles }: { tiles?: boolean } = {}) => el("div", { className: tiles ? "grid" : "kpis" }, children);

// Meter — доля value из max полосой; color — цвет заполнения (токен), по умолчанию акцент
export const Meter = (value: number, max: number, { color, label }: { color?: string; label?: string } = {}) => el("div", { className: "track", role: "meter",
  "aria-valuenow": value, "aria-valuemin": 0, "aria-valuemax": max, "aria-label": label },
el("s", { style: `width:${max ? (value / max) * 100 : 0}%${color ? `;background:${color}` : ""}` }));

export interface FunnelRow {
  label: string;
  value: number;
  base: number | null;
  color?: string;
}

// Funnel — шаги воронки; полоса — от max, % — от base (null — 100%)
export function Funnel(rows: FunnelRow[], { max, note }: { max?: number; note?: string } = {}): HTMLDivElement {
  const top = Math.max(1, max ?? Math.max(...rows.map((r) => r.value)));
  return el("div", { className: "funnel" },
    rows.map((r) => el("div", { className: "f" },
      el("em", { textContent: r.label }),
      Meter(r.value, top, { color: r.color, label: r.label }),
      el("b", { textContent: String(r.value) }),
      el("small", { textContent: r.base == null ? "100%" : `${pct(r.value, r.base)}%` }))),
    note ? el("div", { className: "muted fnote", textContent: note }) : null);
}

export interface BarItem {
  total: number;
  parts?: { value: number; tone?: "ok" | "accent" }[];
  title?: string;
}

// Bars — столбцы с накоплением: части снизу вверх; фон столбца — total, части — доля от total.
// from/to — подписи оси, legend — подписи цветов
export function Bars(items: BarItem[], { from, to, legend }: { from?: string; to?: string; legend?: { label: string; color: string }[] } = {}): HTMLDivElement {
  const top = Math.max(1, ...items.map((i) => i.total));
  return el("div", {},
    el("div", { className: cls("bars", items.length > 20 && "dense") }, items.map((i) => el("div", { style: `height:${(i.total / top) * 100}%`, title: i.title },
      (i.parts ?? []).map((p) => el("u", { className: p.tone === "ok" ? "ok" : "", style: `height:${i.total ? (p.value / i.total) * 100 : 0}%` }))))),
    from || to ? el("div", { className: "days" }, el("span", { textContent: from ?? "" }), el("span", { textContent: to ?? "" })) : null,
    legend ? el("div", { className: "legend" }, legend.map((l) => el("span", {}, el("i", { style: `background:${l.color}` }), l.label))) : null);
}

// RankList — строки «текст … число»
export const RankList = (items: [string, number][]) =>
  el("ul", { className: "reasons" }, items.map(([text, n]) => el("li", {}, el("span", { textContent: text }), el("b", { className: "mono", textContent: String(n) }))));

// ── FilterList, PickRow, SelectionBar (#127) ──────────────────────────
export interface FilterItem {
  href: string;
  label: string;
  count: number;
  active?: boolean;
}

// FilterList — фильтры ссылками со счётчиком и полосой доли (от max, по умолчанию — наибольший счётчик); на телефоне — строка чипов
export function FilterList(items: FilterItem[], { max, label }: { max?: number; label?: string } = {}): HTMLElement {
  const top = Math.max(1, max ?? Math.max(...items.map((i) => i.count)));
  return el("nav", { className: "filters", "aria-label": label },
    items.map((i) => el("a", { href: i.href, className: i.active ? "active" : "", "aria-current": i.active ? "true" : undefined },
      el("span", { textContent: i.label }), el("b", { className: "mono", textContent: String(i.count) }), Meter(i.count, top, { label: i.label }))));
}

export interface PickOptions {
  title: string;
  href?: string;
  meta?: MetaParts;
  note?: Child;
  // Число справа (балл) и его тон
  score?: number | string | null;
  tone?: Tone;
  actions?: Child;
  checked?: boolean;
  onToggle: (on: boolean) => void;
  // Подпись флажка для чтения с экрана; по умолчанию — заголовок
  label?: string;
}

export type PickElement = HTMLDivElement & { checked: boolean };

// PickRow — строка списка с флажком выбора: заголовок (ссылка), Meta, пояснение, число и действия справа.
// Выбранная подсвечивается; .checked = … меняет выбор без onToggle. На телефоне — карточка
export function PickRow({ title, href, meta, note, score, tone, actions, checked = false, onToggle, label }: PickOptions): PickElement {
  const box = el("input", { type: "checkbox", checked, "aria-label": label ?? title });
  const row = el("div", { className: cls("pick", checked && "on"), tabIndex: 0 },
    box,
    el("div", { className: "pick-body" },
      href ? el("a", { href: safeUrl(href), target: "_blank", rel: "noopener", className: "pick-title", textContent: title }) : el("b", { className: "pick-title", textContent: title }),
      Meta(meta), note ? el("div", { className: "reason" }, note) : null),
    el("div", { className: "pick-aside" }, score != null ? el("b", { className: cls("mono", tone), textContent: String(score) }) : null, actions)) as PickElement;
  Object.defineProperty(row, "checked", {
    get: () => box.checked,
    set: (on: boolean) => { box.checked = on; row.classList.toggle("on", on); },
  });
  box.onchange = () => { row.classList.toggle("on", box.checked); onToggle(box.checked); };
  return row;
}

export type SelectionBarElement = HTMLDivElement & { update(count: number, total: number): void; dispose(): void };

// SelectionBar — «N выбрано» с флажком «выбрать все» и действиями над выбранным. Действия — функция: на компьютере они в строке,
// на телефоне — в нижнем листе по кнопке. update(count, total) — перерисовать счётчик; dispose() — убрать лист
export function SelectionBar(actions: () => Child[], { onAll, labels = {} }: { onAll: (on: boolean) => void; labels?: { selected?: string; all?: string; more?: string } }): SelectionBarElement {
  const all = el("input", { type: "checkbox", "aria-label": labels.all ?? "Выбрать все" });
  const text = el("span", { className: "selbar-count" });
  const sheet = Sheet(el("div", { className: "sheet-body actions" }, actions()), { title: labels.more ?? "Действия" });
  const more = Button(labels.more ?? "Действия", { kind: "primary", className: "selbar-more", onClick: (e) => { e.stopPropagation(); sheet.toggle(); } });
  const bar = el("div", { className: "selbar" }, all, text, el("div", { className: "selbar-acts actions flush" }, actions()), more, sheet) as SelectionBarElement;
  all.onchange = () => onAll(all.checked);
  bar.update = (count, total) => {
    text.textContent = count ? `${count} ${labels.selected ?? "выбрано"}` : labels.all ?? "Выбрать все";
    all.checked = total > 0 && count === total;
    all.indeterminate = count > 0 && count < total;
    bar.classList.toggle("on", count > 0);
    bar.querySelectorAll<HTMLButtonElement>(".selbar-acts button, .sheet button").forEach((b) => { b.disabled = count === 0; });
    more.disabled = count === 0;
    if (!count) sheet.close();
  };
  bar.dispose = () => { sheet.dispose(); bar.remove(); };
  bar.update(0, 0);
  return bar;
}

// ── StackTable ────────────────────────────────────────────────────────
export interface Column {
  title: string;
  num?: boolean;
}
export type Cell = Node | string | number | null | undefined | false;

// Таблица, которая на телефоне становится карточками «поле: значение»: подпись поля — из заголовка столбца.
// rows: массивы ячеек (текст или узлы). Обёртка .table-scroll: на средних ширинах широкая таблица прокручивается внутри карточки,
// а не вылезает за неё (кнопки и метки не переносятся)
export function StackTable(columns: Column[], rows: Cell[][]): HTMLDivElement {
  return el("div", { className: "table-scroll" }, el("table", { className: "stack" },
    el("thead", {}, el("tr", {}, columns.map((c) => el("th", { className: c.num ? "n" : "", textContent: c.title })))),
    el("tbody", {}, rows.map((cells) => el("tr", {}, cells.map((cell, i) => {
      const td = el("td", { className: columns[i]?.num ? "n" : "", "data-label": columns[i]?.title ?? "" });
      if (cell != null && cell !== false) td.append(typeof cell === "number" ? String(cell) : cell);
      return td;
    }))))));
}

// ── Field, Form ───────────────────────────────────────────────────────
// Field — подпись и поле: в сетке FormGrid (wide — на всю ширину) или stacked — подпись над полем
let fieldIds = 0;
export function Field(label: string, control: HTMLElement, { wide, stacked }: { wide?: boolean; stacked?: boolean } = {}): Node[] {
  control.id ||= `f-${++fieldIds}`;
  if (stacked) return [el("label", { className: "field", for: control.id, textContent: label }), control];
  if (wide) control.classList.add("wide");
  return [el("label", { for: control.id, className: wide ? "wide" : "", textContent: label }), control];
}
export const FormGrid = (...fields: Child[]) => el("div", { className: "form" }, fields);

// Form — форма: onSubmit (может вернуть промис) держит submit занятой; className по умолчанию card
export function Form({ submit, onSubmit, className = "card" }: { submit?: HTMLButtonElement | null; onSubmit: (e: SubmitEvent) => unknown; className?: string },
  ...children: Child[]): HTMLFormElement {
  const form = el("form", { className }, children);
  form.onsubmit = (e) => {
    e.preventDefault();
    return submit ? busy(submit, () => onSubmit(e)) : onSubmit(e);
  };
  return form;
}

// ── Toast, Sheet ──────────────────────────────────────────────────────
export interface ToastOptions {
  // «Отменить»: вызывается один раз и закрывает тост
  undo?: () => unknown;
  tone?: "ok" | "bad";
  timeout?: number;
  // Куда класть: document.body или shadow root
  root?: ParentNode;
  labels?: { undo?: string; close?: string };
}

// Toast — короткий итог внизу экрана; labels — подписи кнопок
export function Toast(text: string, { undo, tone, timeout = 6000, root = document.body, labels = {} }: ToastOptions = {}): HTMLDivElement {
  // Контейнер — прямой ребёнок root (без :scope — его понимают не все DOM, в том числе happy-dom в тестах)
  let box = [...root.children].find((c): c is HTMLDivElement => c.classList.contains("toasts"));
  if (!box) root.append(box = el("div", { className: "toasts", role: "status", "aria-live": "polite" }));
  const close = (): void => toast.remove();
  // Отмена — ровно один раз: повторное нажатие (двойной клик, клавиатура) уже ничего не делает
  let undone = false;
  const toast: HTMLDivElement = el("div", { className: cls("toast", tone) }, el("span", { textContent: text }),
    undo ? Button(labels.undo ?? "Отменить", { kind: "ghost", size: "sm", icon: "undo", onClick: async () => {
      if (undone) return;
      undone = true;
      close();
      await undo();
    } }) : null,
    Button(labels.close ?? "Закрыть", { kind: "ghost", size: "sm", icon: "x", iconOnly: true, onClick: close }));
  box.append(toast);
  if (timeout) setTimeout(close, timeout);
  return toast;
}

export type SheetElement = HTMLDivElement & { open(): void; close(): void; toggle(): void; dispose(): void };

// Sheet — нижний лист: на телефоне — над нижней панелью, на компьютере — в углу. Закрывается кликом мимо и Esc
// (клик внутри shadow root узнаётся по composedPath). Узел с методами open() / close() / toggle(); вставляет вызывающий
export function Sheet(children: Child, { title, label }: { title?: string; label?: string } = {}): SheetElement {
  const sheet = el("div", { className: "sheet", hidden: true, role: "dialog", "aria-label": label ?? title },
    title ? Caption(title) : null, children) as SheetElement;
  sheet.open = () => { sheet.hidden = false; };
  sheet.close = () => { sheet.hidden = true; };
  sheet.toggle = () => { sheet.hidden = !sheet.hidden; };
  const outside = (e: MouseEvent) => { if (!sheet.hidden && !e.composedPath().includes(sheet)) sheet.close(); };
  const escape = (e: KeyboardEvent) => { if (e.key === "Escape") sheet.close(); };
  document.addEventListener("click", outside);
  document.addEventListener("keydown", escape);
  // Убрать слушатели, когда лист больше не нужен
  sheet.dispose = () => { document.removeEventListener("click", outside); document.removeEventListener("keydown", escape); sheet.remove(); };
  return sheet;
}
// Пункты листа-меню
export const SheetLinks = (items: NavItem[]) => items.map((i) => el("a", { href: i.href, className: i.active ? "active" : "" }, i.icon ? Icon(i.icon) : null, i.label, Count(i.count), Alert(i.alert)));

// ── Menu ──────────────────────────────────────────────────────────────
// Пункт меню: ссылка (href) или действие (onClick). keepOpen — меню не закрывается после действия (переключатель темы).
// "sep" — разделитель
export interface MenuItem {
  label: string;
  icon?: IconName;
  href?: string;
  download?: boolean;
  onClick?: (item: HTMLElement) => unknown;
  keepOpen?: boolean;
  active?: boolean;
  danger?: boolean;
}
export type MenuEntry = MenuItem | "sep";

// Пункты меню — и для Menu, и для Sheet (на телефоне меню аккаунта — в листе «Ещё»); onPick — после выбора пункта
export function MenuItems(items: MenuEntry[], onPick: () => void = () => {}): Node[] {
  return items.map((i) => {
    if (i === "sep") return el("hr", { className: "menu-sep" });
    const body = [i.icon ? Icon(i.icon) : null, el("span", { textContent: i.label })];
    const className = cls("item", i.active && "active", i.danger && "danger");
    if (i.href) {
      const a = el("a", { href: i.href, className, download: i.download ? "" : undefined, role: "menuitem" }, body);
      a.addEventListener("click", () => onPick());
      return a;
    }
    const button: HTMLButtonElement = el("button", { type: "button", className, role: "menuitem" }, body);
    button.onclick = async (e) => { e.stopPropagation(); await i.onClick?.(button); if (!i.keepOpen) onPick(); };
    return button;
  });
}

export type MenuElement = HTMLDivElement & { open(): void; close(): void; dispose(): void };

// Menu — выпадающее меню под кнопкой (меню аккаунта под именем в шапке). Закрывается выбором пункта, кликом мимо и Esc
// button — триггер в виде обычной кнопки (действие с выбором варианта: «Оценить заново через ▾», #107)
export function Menu(trigger: Child, items: MenuEntry[], { label, align = "end", button: asButton }: { label?: string; align?: "start" | "end"; button?: boolean } = {}): MenuElement {
  const button = el("button", { type: "button", className: cls("menu-trigger", asButton && "as-button"), "aria-haspopup": "menu", "aria-expanded": "false", "aria-label": label }, trigger, Icon("chevron"));
  const list = el("div", { className: cls("menu-list", align), role: "menu", hidden: true });
  const menu = el("div", { className: "menu" }, button, list) as MenuElement;
  menu.open = () => { list.hidden = false; button.setAttribute("aria-expanded", "true"); };
  menu.close = () => { list.hidden = true; button.setAttribute("aria-expanded", "false"); };
  list.append(...MenuItems(items, menu.close));
  button.onclick = (e) => { e.stopPropagation(); if (list.hidden) menu.open(); else menu.close(); };
  const outside = (e: MouseEvent) => { if (!list.hidden && !e.composedPath().includes(menu)) menu.close(); };
  const escape = (e: KeyboardEvent) => { if (e.key === "Escape" && !list.hidden) { menu.close(); button.focus(); } };
  document.addEventListener("click", outside);
  document.addEventListener("keydown", escape);
  menu.dispose = () => { document.removeEventListener("click", outside); document.removeEventListener("keydown", escape); menu.remove(); };
  return menu;
}

// ── Confirm, KeyHints ─────────────────────────────────────────────────
export interface ConfirmOptions {
  title: string;
  text?: string;
  // Подпись кнопки подтверждения и её вид (danger — необратимое)
  confirm?: string;
  cancel?: string;
  kind?: ButtonKind;
  // Поле в диалоге (новый пароль): проверяется формой (required, minLength) до подтверждения
  field?: HTMLInputElement | null;
  root?: ParentNode;
}

// Confirm — подтверждение действия без обратного (удалить, отозвать) вместо confirm()/prompt() браузера.
// Модальный диалог по центру, на телефоне — у нижнего края. Esc и клик мимо — отмена. Промис: true — подтвердили
export function Confirm({ title, text, confirm = "Подтвердить", cancel = "Отмена", kind = "primary", field, root = document.body }: ConfirmOptions): Promise<boolean> {
  return new Promise((resolve) => {
    const before = document.activeElement as HTMLElement | null;
    const titleId = `dlg-${++fieldIds}`;
    const ok = Button(confirm, { kind, type: "submit" });
    const no = Button(cancel, { kind: "ghost" });
    const form = el("form", { className: "dialog", role: "dialog", "aria-modal": "true", "aria-labelledby": titleId },
      el("h2", { id: titleId, textContent: title }), text ? el("p", { textContent: text }) : null,
      field ? Field(field.getAttribute("aria-label") ?? "", field, { stacked: true }) : null,
      el("div", { className: "actions flush" }, no, ok));
    const backdrop = el("div", { className: "dialog-backdrop" }, form);
    const finish = (value: boolean) => {
      document.removeEventListener("keydown", escape, true);
      backdrop.remove();
      before?.focus?.();
      resolve(value);
    };
    const escape = (e: KeyboardEvent) => { if (e.key === "Escape") { e.stopPropagation(); finish(false); } };
    form.onsubmit = (e) => { e.preventDefault(); finish(true); };
    no.onclick = () => finish(false);
    backdrop.onclick = (e) => { if (e.target === backdrop) finish(false); };
    document.addEventListener("keydown", escape, true);
    root.append(backdrop);
    (field ?? ok).focus();
  });
}

// KeyHints — подсказка клавиш в шапке раздела: [клавиши, что делают]; на телефоне скрыта
export const KeyHints = (items: [string, string][], { label = "Клавиши" }: { label?: string } = {}) =>
  el("div", { className: "keys", role: "note", "aria-label": label },
    items.map(([keys, text]) => el("span", {}, keys.split("/").map((k) => el("kbd", { textContent: k })), " ", text)));

// ── SearchButton, CommandPalette (#128) ──────────────────────────────
// SearchButton — кнопка в виде поля поиска в шапке: значок, подсказка и клавиша; открывает палитру команд.
// На узкой ширине подпись и клавиша прячутся (CSS), остаётся значок; подпись — и в aria-label
export function SearchButton(text: string, { keys = "Ctrl K", onClick }: { keys?: string; onClick?: () => unknown } = {}): HTMLButtonElement {
  const button = el("button", { type: "button", className: "search-button", "aria-label": `${text} (${keys})`, "aria-haspopup": "dialog", title: keys },
    Icon("search"), el("span", { className: "search-text", textContent: text }), el("kbd", { textContent: keys }));
  if (onClick) button.onclick = (e) => { e.stopPropagation(); onClick(); };
  return button;
}

// Пункт палитры: ссылка (href; external — в новой вкладке) или действие (onRun). keywords — дополнительные слова для поиска
export interface PaletteItem {
  label: string;
  hint?: string;
  icon?: IconName;
  href?: string;
  external?: boolean;
  onRun?: () => unknown;
  keywords?: string;
}
export interface PaletteGroup {
  title: string;
  items: PaletteItem[];
}
// Источник с запросом (поиск по вакансиям): зовётся с введённым текстом не короче min символов, после паузы ввода.
// signal отменяется, когда запрос устарел (новый ввод, закрытие палитры)
export interface PaletteSource {
  title: string;
  min?: number;
  search: (query: string, signal: AbortSignal) => Promise<PaletteItem[]>;
}

const norm = (s: string) => s.toLocaleLowerCase("ru-RU").replace(/ё/g, "е");

// Фильтр палитры: каждое слово запроса — подстрока подписи, подсказки или keywords (без учёта регистра, «ё» = «е»).
// Пустые группы пропускаются; пустой запрос — всё как есть
export function filterPalette(groups: PaletteGroup[], query: string): PaletteGroup[] {
  const words = norm(query).split(/\s+/).filter(Boolean);
  const match = (i: PaletteItem) => {
    const hay = norm(`${i.label} ${i.hint ?? ""} ${i.keywords ?? ""}`);
    return words.every((w) => hay.includes(w));
  };
  return groups.map((g) => ({ title: g.title, items: words.length ? g.items.filter(match) : g.items })).filter((g) => g.items.length);
}

export type PaletteElement = HTMLDivElement & { open(): void; close(): void; toggle(): void; readonly isOpen: boolean; dispose(): void };
export interface PaletteOptions {
  // Группы пунктов — функцией: права и подписи (текущая тема) берутся в момент открытия и ввода
  groups: () => PaletteGroup[];
  sources?: PaletteSource[];
  placeholder?: string;
  label?: string;
  // Ctrl+K / Cmd+K открывает и закрывает палитру (по коду клавиши — работает и в русской раскладке)
  hotkey?: boolean;
  root?: ParentNode;
  // Пауза ввода перед запросом к источникам, мс
  delay?: number;
}

let paletteIds = 0;

// CommandPalette — палитра команд: модальный диалог с полем и списком (combobox + listbox). Ввод фильтрует, ↑↓ выбирают,
// Enter выполняет, Esc и клик мимо закрывают; при открытии фокус в поле, при закрытии — туда, где был.
// Ссылка выбирается кликом по <a> — переход делает тот же обработчик ссылок, что у остальной страницы. Данные — только текстом
export function CommandPalette({ groups, sources = [], placeholder = "Найти раздел или команду…", label = "Палитра команд",
  hotkey = true, root = document.body, delay = 200 }: PaletteOptions): PaletteElement {
  const id = `pal-${++paletteIds}`;
  const input = el("input", { type: "text", className: "palette-input", placeholder, autocomplete: "off", spellcheck: false,
    role: "combobox", "aria-label": label, "aria-expanded": "true", "aria-controls": `${id}-list`, "aria-autocomplete": "list" });
  const list = el("div", { id: `${id}-list`, className: "palette-list", role: "listbox", "aria-label": "Результаты" });
  const box = el("div", { className: "palette", role: "dialog", "aria-modal": "true", "aria-label": label },
    el("div", { className: "palette-head" }, Icon("search"), input), list,
    el("div", { className: "palette-foot" }, KeyHints([["↑/↓", "выбор"], ["Enter", "открыть"], ["Esc", "закрыть"]])));
  const backdrop = el("div", { className: "palette-backdrop", hidden: true }, box) as unknown as PaletteElement;

  let options: HTMLElement[] = [];
  let active = 0;
  let before: HTMLElement | null = null;
  let found: PaletteGroup[] = [];
  let timer: ReturnType<typeof setTimeout> | undefined;
  let asked = 0;
  let pending: AbortController | null = null;
  // Отменить запрос к источникам, если он ещё идёт
  const cancel = () => { pending?.abort(); pending = null; };

  const setActive = (i: number) => {
    if (!options.length) { input.removeAttribute("aria-activedescendant"); return; }
    active = (i + options.length) % options.length;
    options.forEach((o, n) => o.setAttribute("aria-selected", String(n === active)));
    input.setAttribute("aria-activedescendant", options[active].id);
    options[active].scrollIntoView?.({ block: "nearest" });
  };

  const option = (item: PaletteItem, n: number): HTMLElement => {
    const body = [item.icon ? Icon(item.icon) : null, el("span", { className: "palette-label", textContent: item.label }),
      item.hint ? el("span", { className: "palette-hint", textContent: item.hint }) : null];
    const props = { id: `${id}-o${n}`, className: "palette-item", role: "option", "aria-selected": "false", tabIndex: -1 };
    const node: HTMLElement = item.href
      // Путь своего сайта (/llm/rejects) — как есть, остальное — только http(s)
      ? el("a", { ...props, href: /^\/(?!\/)/.test(item.href) ? item.href : safeUrl(item.href), target: item.external ? "_blank" : undefined, rel: item.external ? "noopener" : undefined }, body)
      : el("button", { ...props, type: "button" }, body);
    node.addEventListener("click", async () => {
      backdrop.close();
      await item.onRun?.();
    });
    node.addEventListener("mousemove", () => { if (active !== n) setActive(n); });
    return node;
  };

  const draw = () => {
    const shown = [...filterPalette(groups(), input.value), ...found];
    options = [];
    list.replaceChildren(...(shown.length
      ? shown.map((g) => el("div", { className: "palette-group", role: "group", "aria-label": g.title },
        el("div", { className: "palette-caption", textContent: g.title, "aria-hidden": "true" }),
        g.items.map((item) => { const node = option(item, options.length); options.push(node); return node; })))
      : [el("p", { className: "palette-empty", textContent: "Ничего не найдено" })]));
    setActive(0);
  };

  // Источники с запросом: после паузы ввода; ответ на устаревший запрос не рисуется
  const ask = () => {
    clearTimeout(timer);
    cancel();
    found = [];
    const query = input.value.trim();
    const ready = sources.filter((s) => query.length >= (s.min ?? 2));
    const ticket = ++asked;
    if (!ready.length) return;
    timer = setTimeout(async () => {
      const controller = pending = new AbortController();
      const results = await Promise.all(ready.map(async (s) => ({ title: s.title, items: await s.search(query, controller.signal).catch(() => [] as PaletteItem[]) })));
      if (pending === controller) pending = null;
      if (ticket !== asked || backdrop.hidden) return;
      found = results.filter((g) => g.items.length);
      draw();
    }, delay);
  };

  input.addEventListener("input", () => { ask(); draw(); });
  input.addEventListener("keydown", (e) => {
    if (e.key === "ArrowDown") { e.preventDefault(); setActive(active + 1); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setActive(active - 1); }
    else if (e.key === "Enter") { e.preventDefault(); options[active]?.click(); }
    // Фокус остаётся в поле: выбор — стрелками
    else if (e.key === "Tab") e.preventDefault();
  });
  backdrop.addEventListener("click", (e) => { if (e.target === backdrop) backdrop.close(); });

  backdrop.open = () => {
    if (!backdrop.hidden) return;
    before = document.activeElement as HTMLElement | null;
    input.value = "";
    found = [];
    backdrop.hidden = false;
    draw();
    input.focus();
  };
  backdrop.close = () => {
    if (backdrop.hidden) return;
    clearTimeout(timer);
    cancel();
    asked++;
    backdrop.hidden = true;
    before?.focus?.();
    before = null;
  };
  backdrop.toggle = () => (backdrop.hidden ? backdrop.open() : backdrop.close());
  Object.defineProperty(backdrop, "isOpen", { get: () => !backdrop.hidden });

  // Esc — в фазе захвата, чтобы не закрыть заодно меню или лист под палитрой
  const keys = (e: KeyboardEvent) => {
    if (e.key === "Escape" && !backdrop.hidden) { e.stopPropagation(); e.preventDefault(); backdrop.close(); return; }
    if (hotkey && (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && (e.code === "KeyK" || e.key?.toLowerCase() === "k")) {
      e.preventDefault();
      backdrop.toggle();
    }
  };
  document.addEventListener("keydown", keys, true);
  backdrop.dispose = () => { document.removeEventListener("keydown", keys, true); clearTimeout(timer); cancel(); backdrop.remove(); };
  root.append(backdrop);
  return backdrop;
}
