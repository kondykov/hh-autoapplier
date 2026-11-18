// UI kit (#129): компоненты-функции поверх el() — ES-модуль без фреймворков, сборки и побочных эффектов при импорте.
// Общий для веба (<script type="module">) и расширения (импорт через esbuild): нет состояния приложения, fetch и API —
// данные, тексты и обработчики приходят параметрами. Работает в обычном документе и в shadow root.
// Стили — tokens.css и ui.css рядом (в вебе — <link>, в shadow root — injectStyles), иконки — спрайт icons.svg.
// Безопасность: данные попадают в узлы только текстом (textContent / текстовые дети el), ссылки — через safeUrl

// Узел из тега, свойств и детей; дети-строки становятся текстом, null/false/undefined пропускаются
export function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === undefined) continue;
    if (key.startsWith("data-") || key.startsWith("aria-") || key === "for" || key === "role") node.setAttribute(key, value);
    else node[key] = value;
  }
  for (const child of children.flat(Infinity)) if (child != null && child !== false) node.append(child);
  return node;
}

// Ссылка — только http(s): javascript: и прочее из данных не попадает в href
export function safeUrl(url) {
  try {
    const u = new URL(url);
    return u.protocol === "https:" || u.protocol === "http:" ? u.href : "#";
  } catch {
    return "#";
  }
}

export const cls = (...names) => names.filter(Boolean).join(" ");
export const pct = (a, b) => (b ? Math.round((a / b) * 100) : 0);

// ── Стили и иконки в shadow root ─────────────────────────────────────
// Веб подключает tokens.css и ui.css через <link>. Shadow root и страницы расширения — текстом (esbuild loader "text"):
// injectStyles(root, [tokensCss, uiCss]). Повторный вызов с тем же root ничего не дублирует
export function injectStyles(root, cssTexts) {
  const target = root instanceof Document ? root.head : root;
  let style = target.querySelector?.("style[data-ui-kit]");
  if (!style) target.prepend(style = el("style", { "data-ui-kit": "" }));
  style.textContent = [].concat(cssTexts).join("\n");
  return style;
}

// Иконки: по умолчанию — внешний спрайт рядом со страницей (веб, страницы расширения: setIconSprite(chrome.runtime.getURL(...))).
// В shadow root на чужом сайте внешний спрайт другого источника не грузится — тогда injectIcons(root, svgText) встраивает
// спрайт в root, и Icon ссылается на #имя внутри него
export const ICONS = ["dashboard", "review", "dialogs", "vacancies", "overview", "llm", "search", "resume", "user", "admin", "link", "external",
  "clock", "check", "x", "undo", "more", "plus", "copy", "alert", "hash", "theme", "moon", "sun", "logout", "kit"];
let sprite = "ui/icons.svg";
export function setIconSprite(url) { sprite = url; }
export function injectIcons(root, svgText) {
  const target = root instanceof Document ? root.body : root;
  target.querySelector?.("[data-ui-icons]")?.remove();
  const holder = el("div", { "data-ui-icons": "", hidden: true, "aria-hidden": "true" });
  // Текст спрайта — свой файл kit, не данные
  holder.innerHTML = svgText;
  target.append(holder);
  sprite = "";
}

// Иконка из спрайта; title — подпись для чтения с экрана, без неё иконка декоративная
export function Icon(name, { title, size } = {}) {
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
// Занятость: на время действия узел выключен и помечен aria-busy, после — включён (даже если действие упало)
export async function busy(node, action) {
  node.disabled = true;
  node.setAttribute("aria-busy", "true");
  try {
    return await action();
  } finally {
    node.disabled = false;
    node.removeAttribute("aria-busy");
  }
}

// kind: primary | ok | danger | ghost; size: sm | lg; icon — имя иконки слева (iconOnly — только иконка, text уходит в подпись).
// onClick, вернувший промис, держит кнопку занятой до его завершения
export function Button(text, { kind, size, icon, iconOnly, type = "button", title, disabled, onClick, className } = {}) {
  const b = el("button", {
    type, title: title ?? (iconOnly ? text : undefined), disabled: !!disabled,
    className: cls(kind, size, iconOnly && "icon", icon && !iconOnly && "with-icon", className) || undefined,
    "aria-label": iconOnly ? text : undefined,
  }, icon ? Icon(icon) : null, iconOnly ? null : text);
  if (onClick) b.onclick = (e) => {
    const result = onClick(e);
    if (result && typeof result.then === "function") return busy(b, () => result);
    return result;
  };
  return b;
}

// Ряды кнопок: Actions — под карточкой, Toolbar — фильтры и формы в строку. flush — без внешних отступов, after — отступ сверху
export const Actions = (children, { flush } = {}) => el("div", { className: cls("actions", flush && "flush") }, children);
export const Toolbar = (children, { flush, after } = {}) => el("div", { className: cls("bar", flush && "flush", after && "after") }, children);
// Итог действия рядом с кнопками (текст меняет вызывающий)
export const Result = (text = "") => el("span", { className: "result", textContent: text });

// ── Pill, Badge, Count ────────────────────────────────────────────────
// Pill — статус моноширинной меткой; tone: ok | bad | accent; dot — цвет точки (например, источник)
export function Pill(text, { tone, dot, title } = {}) {
  return el("span", { className: cls("pill", dot && "src", tone), title },
    dot ? el("i", { style: `background:${dot}` }) : null, text);
}
// Badge — залитая метка; tone: ok | accent | bad
export const Badge = (text, { tone } = {}) => el("span", { className: cls("kind", tone), textContent: text });
// Count — счётчик; 0 — ничего
export const Count = (n) => (n ? el("span", { className: "count", textContent: n > 99 ? "99+" : n }) : null);

// ── Card и тексты ─────────────────────────────────────────────────────
// Строка «серым»: части через « · », пустые выкидываются; lead — узел в начале строки (метка)
export function Meta(parts, { lead } = {}) {
  if (parts == null) return null;
  const text = Array.isArray(parts) ? parts.filter(Boolean).join(" · ") : String(parts);
  return el("div", { className: "meta" }, lead ?? null, text);
}

// as: article | form | div; title — заголовок h2 (href — ссылкой в новой вкладке) или head — свой узел шапки;
// meta — строка или список частей; actions — ряд кнопок внизу; decided — «решено» (приглушено); tight — плотная
export function Card({ as = "div", title, href, head, meta, actions, decided, tight, className, ...props } = {}, ...children) {
  const heading = head ?? (title == null ? null
    : el("h2", {}, href ? el("a", { href: safeUrl(href), target: "_blank", rel: "noopener", textContent: title }) : title));
  return el(as, { className: cls("card", tight && "tight", decided && "decided", className), ...props },
    heading, Meta(meta), children, actions ? Actions(actions) : null);
}
Card.decide = (node) => node.closest(".card")?.classList.add("decided");

// Заголовок страницы: h1 и подзаголовок; aside — справа (переключатели)
export function PageHead(title, sub, aside) {
  if (!aside) return [el("h1", { textContent: title }), sub ? el("p", { className: "sub", textContent: sub }) : null];
  return el("div", { className: "page-head" },
    el("div", {}, el("h1", { textContent: title }), sub ? el("div", { className: "muted", textContent: sub }) : null), aside);
}
// Подпись блока внутри карточки
export const Caption = (text) => el("h3", { textContent: text });

// Callout — вывод на жёлтом, Quote — цитата сообщения, Pre — длинный текст с переносами (scroll — с прокруткой),
// Secret — токен или код, Note — служебная пометка, Disclosure — раскрывающийся блок
export const Callout = (text) => el("div", { className: "callout", textContent: text });
export const Quote = (text) => el("div", { className: "quote", textContent: text });
export const Pre = (text, { scroll } = {}) => el("div", { className: cls("pre", scroll && "scroll"), textContent: text });
export const Secret = (text) => el("span", { className: "secret", textContent: text });
export const Note = (text) => el("div", { className: "note", textContent: text });
export const Disclosure = (summary, children, { open } = {}) => el("details", { open: !!open }, el("summary", { textContent: summary }), children);

// ── EmptyState, Skeleton ──────────────────────────────────────────────
export const EmptyState = (text, { icon } = {}) => el("p", { className: "empty" }, icon ? Icon(icon) : null, text);
// lines — строки текста, block — плашка (карточка или график); label — подпись для чтения с экрана
export const Skeleton = ({ lines = 3, block = false, label = "Загрузка" } = {}) => el("div", { className: "skel", "aria-busy": "true", "aria-label": label },
  block ? el("i", { className: "block" }) : null, Array.from({ length: lines }, () => el("i")));

// ── Tabs, TabBar, Segmented, Chips ────────────────────────────────────
// Tabs — ссылки разделов: items [{ href, label, active, count }]
export const Tabs = (items, { label } = {}) => el("div", { className: "tabs", role: label ? "group" : undefined, "aria-label": label },
  items.map((t) => el("a", { href: t.href, className: t.active ? "active" : "", "aria-current": t.active ? "page" : undefined }, t.label, Count(t.count))));

// TabBar — пункты нижней панели телефона: items [{ href, icon, label, active, count }] и, последним, свой узел (кнопка «Ещё»)
export const TabBar = (items, more) => [
  ...items.map((t) => el("a", { href: t.href, className: t.active ? "active" : "", "aria-current": t.active ? "page" : undefined },
    Icon(t.icon), t.label, Count(t.count))),
  more ?? null,
].filter(Boolean);

// Выбор одного из кнопок: подсветка и aria-pressed переключаются на месте, onChange(value) — дальше решает вызывающий
function choice(options, value, onChange, make) {
  const buttons = options.map((o) => {
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

// Segmented — один из нескольких: options [{ value, label }]
export const Segmented = (options, value, onChange, { label } = {}) => el("div", { className: "seg", role: "group", "aria-label": label },
  choice(options, value, onChange, (o) => el("button", { textContent: o.label })));

// Chips — фильтр кнопками с цветной точкой: options [{ value, label, color, disabled, title }]; label — подпись слева
export const Chips = (options, value, onChange, { label } = {}) => el("div", { className: "chips", role: "group", "aria-label": label },
  label ? el("span", { className: "muted", textContent: label }) : null,
  choice(options, value, onChange, (o) => el("button", { disabled: !!o.disabled, title: o.title ?? "" }, o.color ? el("i", { style: `background:${o.color}` }) : null, o.label)));

// ── Stat, Meter, Funnel, Bars, RankList ───────────────────────────────
// Stat — KPI: value (+ unit мелко), label, note — строка ниже; trend: up | down — цвет строки. href — компактная плитка-ссылка
export function Stat({ value, unit, label, note, trend, href }) {
  if (href) return el("a", { className: "tile", href }, el("b", { textContent: value }), el("span", { textContent: label }));
  return el("div", { className: "card kpi" },
    el("b", {}, String(value), unit ? el("small", { textContent: unit }) : null),
    el("span", { textContent: label }),
    note != null ? el("i", { className: trend ?? "", textContent: note }) : null);
}
export const Stats = (children, { tiles } = {}) => el("div", { className: tiles ? "grid" : "kpis" }, children);

// Meter — доля value из max полосой; color — цвет заполнения (токен), по умолчанию акцент
export const Meter = (value, max, { color, label } = {}) => el("div", { className: "track", role: "meter",
  "aria-valuenow": value, "aria-valuemin": 0, "aria-valuemax": max, "aria-label": label },
el("s", { style: `width:${max ? (value / max) * 100 : 0}%${color ? `;background:${color}` : ""}` }));

// Funnel — шаги воронки: rows [{ label, value, base, color }]; полоса — от max, % — от base (null — 100%)
export function Funnel(rows, { max, note } = {}) {
  const top = Math.max(1, max ?? Math.max(...rows.map((r) => r.value)));
  return el("div", { className: "funnel" },
    rows.map((r) => el("div", { className: "f" },
      el("em", { textContent: r.label }),
      Meter(r.value, top, { color: r.color, label: r.label }),
      el("b", { textContent: r.value }),
      el("small", { textContent: r.base == null ? "100%" : `${pct(r.value, r.base)}%` }))),
    note ? el("div", { className: "muted fnote", textContent: note }) : null);
}

// Bars — столбцы с накоплением: items [{ total, parts: [{ value, tone: ok | accent }], title }] — части снизу вверх;
// фон столбца — total, части — доля от total. from/to — подписи оси, legend [{ label, color }]
export function Bars(items, { from, to, legend } = {}) {
  const top = Math.max(1, ...items.map((i) => i.total));
  return el("div", {},
    el("div", { className: cls("bars", items.length > 20 && "dense") }, items.map((i) => el("div", { style: `height:${(i.total / top) * 100}%`, title: i.title },
      (i.parts ?? []).map((p) => el("u", { className: p.tone === "ok" ? "ok" : "", style: `height:${i.total ? (p.value / i.total) * 100 : 0}%` }))))),
    from || to ? el("div", { className: "days" }, el("span", { textContent: from ?? "" }), el("span", { textContent: to ?? "" })) : null,
    legend ? el("div", { className: "legend" }, legend.map((l) => el("span", {}, el("i", { style: `background:${l.color}` }), l.label))) : null);
}

// RankList — строки «текст … число»: items [[text, n]]
export const RankList = (items) => el("ul", { className: "reasons" }, items.map(([text, n]) => el("li", {}, el("span", { textContent: text }), el("b", { className: "mono", textContent: n }))));

// ── StackTable ────────────────────────────────────────────────────────
// Таблица, которая на телефоне становится карточками «поле: значение»: подпись поля — из заголовка столбца.
// columns: [{ title, num? }], rows: массивы ячеек (текст или узлы)
export function StackTable(columns, rows) {
  return el("table", { className: "stack" },
    el("thead", {}, el("tr", {}, columns.map((c) => el("th", { className: c.num ? "n" : "", textContent: c.title })))),
    el("tbody", {}, rows.map((cells) => el("tr", {}, cells.map((cell, i) => {
      const td = el("td", { className: columns[i]?.num ? "n" : "", "data-label": columns[i]?.title ?? "" });
      if (cell != null && cell !== false) td.append(cell);
      return td;
    })))));
}

// ── Field, Form ───────────────────────────────────────────────────────
// Field — подпись и поле: в сетке FormGrid (wide — на всю ширину) или stacked — подпись над полем
let fieldIds = 0;
export function Field(label, control, { wide, stacked } = {}) {
  control.id ||= `f-${++fieldIds}`;
  if (stacked) return [el("label", { className: "field", for: control.id, textContent: label }), control];
  if (wide) control.classList.add("wide");
  return [el("label", { for: control.id, className: wide ? "wide" : "", textContent: label }), control];
}
export const FormGrid = (...fields) => el("div", { className: "form" }, fields);

// Form — форма: onSubmit (может вернуть промис) держит submit занятой; className по умолчанию card
export function Form({ submit, onSubmit, className = "card" } = {}, ...children) {
  const form = el("form", { className }, children);
  form.onsubmit = (e) => {
    e.preventDefault();
    return submit ? busy(submit, () => onSubmit(e)) : onSubmit(e);
  };
  return form;
}

// ── Toast, Sheet ──────────────────────────────────────────────────────
// Toast — короткий итог внизу экрана; undo — действие «Отменить» (вызывается один раз и закрывает тост); tone: ok | bad.
// root — куда класть (document.body или shadow root); labels — подписи кнопок
export function Toast(text, { undo, tone, timeout = 6000, root = document.body, labels = {} } = {}) {
  let box = root.querySelector(":scope > .toasts");
  if (!box) root.append(box = el("div", { className: "toasts", role: "status", "aria-live": "polite" }));
  const close = () => toast.remove();
  const toast = el("div", { className: cls("toast", tone) }, el("span", { textContent: text }),
    undo ? Button(labels.undo ?? "Отменить", { kind: "ghost", size: "sm", icon: "undo", onClick: async () => { close(); await undo(); } }) : null,
    Button(labels.close ?? "Закрыть", { kind: "ghost", size: "sm", icon: "x", iconOnly: true, onClick: close }));
  box.append(toast);
  if (timeout) setTimeout(close, timeout);
  return toast;
}

// Sheet — нижний лист: на телефоне — над нижней панелью, на компьютере — в углу. Закрывается кликом мимо и Esc
// (клик внутри shadow root узнаётся по composedPath). Узел с методами open() / close() / toggle(); вставляет вызывающий
export function Sheet(children, { title, label } = {}) {
  const sheet = el("div", { className: "sheet", hidden: true, role: "dialog", "aria-label": label ?? title }, title ? Caption(title) : null, children);
  sheet.open = () => { sheet.hidden = false; };
  sheet.close = () => { sheet.hidden = true; };
  sheet.toggle = () => { sheet.hidden = !sheet.hidden; };
  const outside = (e) => { if (!sheet.hidden && !e.composedPath().includes(sheet)) sheet.close(); };
  const escape = (e) => { if (e.key === "Escape") sheet.close(); };
  document.addEventListener("click", outside);
  document.addEventListener("keydown", escape);
  // Убрать слушатели, когда лист больше не нужен
  sheet.dispose = () => { document.removeEventListener("click", outside); document.removeEventListener("keydown", escape); sheet.remove(); };
  return sheet;
}
// Пункты листа-меню: items [{ href, label, icon, active }]
export const SheetLinks = (items) => items.map((i) => el("a", { href: i.href, className: i.active ? "active" : "" }, i.icon ? Icon(i.icon) : null, i.label));
