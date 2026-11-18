"use strict";
// UI kit веба (#129): компоненты-функции поверх el(), без фреймворков и сборки. Стили — ui/ui.css, токены — ui/tokens.css,
// иконки — ui/icons.svg. Подключается обычным <script> до app.js: функции — глобальные.
// Безопасность: данные с hh.ru и от LLM попадают в узлы только текстом (textContent / текстовые дети el), ссылки — через safeUrl

// Узел из тега, свойств и детей; дети-строки становятся текстом, null/false пропускаются
function el(tag, props = {}, ...children) {
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
function safeUrl(url) {
  try {
    const u = new URL(url);
    return u.protocol === "https:" || u.protocol === "http:" ? u.href : "#";
  } catch {
    return "#";
  }
}

const cls = (...names) => names.filter(Boolean).join(" ");

// ── Icon ──────────────────────────────────────────────────────────────
// Иконка из спрайта; title — подпись для чтения с экрана, без неё иконка декоративная
const ICONS = ["dashboard", "review", "dialogs", "vacancies", "overview", "llm", "search", "resume", "user", "admin", "link", "external",
  "clock", "check", "x", "undo", "more", "plus", "copy", "alert", "hash", "theme", "moon", "sun", "logout", "kit"];
function Icon(name, { title, size } = {}) {
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
  use.setAttribute("href", `ui/icons.svg#${name}`);
  svg.append(use);
  return svg;
}

// ── Button ────────────────────────────────────────────────────────────
// Занятость: на время действия узел выключен и помечен aria-busy, после — включён (даже если действие упало)
async function busy(node, action) {
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
function Button(text, { kind, size, icon, iconOnly, type = "button", title, disabled, onClick, className } = {}) {
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
const Actions = (children, { flush } = {}) => el("div", { className: cls("actions", flush && "flush") }, children);
const Toolbar = (children, { flush, after } = {}) => el("div", { className: cls("bar", flush && "flush", after && "after") }, children);
// Итог действия рядом с кнопками (текст меняет вызывающий)
const Result = (text = "") => el("span", { className: "result", textContent: text });

// ── Pill, Badge, Count ────────────────────────────────────────────────
// Pill — статус моноширинной меткой; tone: ok | bad | accent; dot — цвет точки (источник вакансии)
function Pill(text, { tone, dot, title } = {}) {
  return el("span", { className: cls("pill", dot && "src", tone), title },
    dot ? el("i", { style: `background:${dot}` }) : null, text);
}
// Badge — залитая метка вида сообщения; tone: ok | accent | bad
const Badge = (text, { tone } = {}) => el("span", { className: cls("kind", tone), textContent: text });
// Count — счётчик у раздела; 0 — ничего
const Count = (n) => (n ? el("span", { className: "count", textContent: n > 99 ? "99+" : n }) : null);

// ── Card ──────────────────────────────────────────────────────────────
// as: article | form | div; title — заголовок h2 (href — ссылкой в новой вкладке) или head — свой узел шапки;
// meta — строка или список частей через « · »; actions — ряд кнопок внизу; decided — состояние «решено» (приглушено)
function Card({ as = "div", title, href, head, meta, actions, decided, tight, className, ...props } = {}, ...children) {
  const heading = head ?? (title == null ? null
    : el("h2", {}, href ? el("a", { href: safeUrl(href), target: "_blank", rel: "noopener", textContent: title }) : title));
  return el(as, { className: cls("card", tight && "tight", decided && "decided", className), ...props },
    heading, Meta(meta), children, actions ? Actions(actions) : null);
}
Card.decide = (node) => node.closest(".card")?.classList.add("decided");

// Строка «серым»: части через « · », пустые выкидываются; lead — узел в начале строки (метка)
function Meta(parts, { lead } = {}) {
  if (parts == null) return null;
  const text = Array.isArray(parts) ? parts.filter(Boolean).join(" · ") : String(parts);
  return el("div", { className: "meta" }, lead ?? null, text);
}

// Заголовок раздела: h1 и подзаголовок; aside — справа (переключатели)
function PageHead(title, sub, aside) {
  if (!aside) return [el("h1", { textContent: title }), sub ? el("p", { className: "sub", textContent: sub }) : null];
  return el("div", { className: "page-head" },
    el("div", {}, el("h1", { textContent: title }), sub ? el("div", { className: "muted", textContent: sub }) : null), aside);
}
// Подпись блока внутри карточки
const Caption = (text) => el("h3", { textContent: text });

// Тексты из данных: Callout — вывод LLM на жёлтом, Quote — сообщение работодателя, Pre — длинный текст с переносами,
// Secret — токен и код, Note — служебная пометка, Disclosure — раскрывающийся блок
const Callout = (text) => el("div", { className: "callout", textContent: text });
const Quote = (text) => el("div", { className: "quote", textContent: text });
const Pre = (text, { scroll } = {}) => el("div", { className: cls("pre", scroll && "scroll"), textContent: text });
const Secret = (text) => el("span", { className: "secret", textContent: text });
const Note = (text) => el("div", { className: "note", textContent: text });
const Disclosure = (summary, children, { open } = {}) => el("details", { open: !!open }, el("summary", { textContent: summary }), children);

// ── EmptyState, Skeleton ──────────────────────────────────────────────
const EmptyState = (text, { icon } = {}) => el("p", { className: "empty" }, icon ? Icon(icon) : null, text);
// lines — строки текста, block — плашка (карточка или график)
const Skeleton = ({ lines = 3, block = false } = {}) => el("div", { className: "skel", "aria-busy": "true", "aria-label": "Загрузка" },
  block ? el("i", { className: "block" }) : null, Array.from({ length: lines }, () => el("i")));

// ── Tabs, TabBar, Segmented, Chips ────────────────────────────────────
// Tabs — ссылки разделов: items [{ href, label, active, count }]
const Tabs = (items, { label } = {}) => el("div", { className: "tabs", role: label ? "group" : undefined, "aria-label": label },
  items.map((t) => el("a", { href: t.href, className: t.active ? "active" : "", "aria-current": t.active ? "page" : undefined }, t.label, Count(t.count))));

// TabBar — нижняя панель телефона: items [{ href, icon, label, active, count }] и кнопка «Ещё» последней
const TabBar = (items, more) => [
  ...items.map((t) => el("a", { href: t.href, className: t.active ? "active" : "", "aria-current": t.active ? "page" : undefined },
    Icon(t.icon), t.label, Count(t.count))),
  more ?? null,
].filter(Boolean);

// Segmented — один из нескольких: options [{ value, label }]; onChange(value)
function Segmented(options, value, onChange, { label } = {}) {
  const root = el("div", { className: "seg", role: "group", "aria-label": label });
  const buttons = options.map((o) => {
    const b = el("button", { type: "button", className: o.value === value ? "on" : "", textContent: o.label, "aria-pressed": String(o.value === value) });
    b.onclick = () => {
      buttons.forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", String(x === b)); });
      onChange(o.value);
    };
    return b;
  });
  root.append(...buttons);
  return root;
}

// Chips — фильтр кнопками с цветной точкой: options [{ value, label, color, disabled, title }]
function Chips(options, value, onChange, { label } = {}) {
  const buttons = options.map((o) => {
    const b = el("button", { type: "button", className: o.value === value ? "on" : "", disabled: !!o.disabled, title: o.title ?? "", "aria-pressed": String(o.value === value) },
      o.color ? el("i", { style: `background:${o.color}` }) : null, o.label);
    b.onclick = () => {
      buttons.forEach((x) => { x.classList.toggle("on", x === b); x.setAttribute("aria-pressed", String(x === b)); });
      onChange(o.value);
    };
    return b;
  });
  return el("div", { className: "chips", role: "group", "aria-label": label }, label ? el("span", { className: "muted", textContent: label }) : null, buttons);
}

// ── Stat, Meter, Funnel, Bars, RankList ───────────────────────────────
// Stat — KPI: value (+ unit мелко), label, note — строка ниже; trend: up | down — цвет строки.
// href — компактная плитка-ссылка (обзор)
function Stat({ value, unit, label, note, trend, href }) {
  if (href) return el("a", { className: "tile", href }, el("b", { textContent: value }), el("span", { textContent: label }));
  return el("div", { className: "card kpi" },
    el("b", {}, String(value), unit ? el("small", { textContent: unit }) : null),
    el("span", { textContent: label }),
    note != null ? el("i", { className: trend ?? "", textContent: note }) : null);
}
const Stats = (children, { tiles } = {}) => el("div", { className: tiles ? "grid" : "kpis" }, children);

// Meter — доля value из max полосой; color — цвет заполнения (токен), по умолчанию акцент
const Meter = (value, max, { color, label } = {}) => el("div", { className: "track", role: "meter",
  "aria-valuenow": value, "aria-valuemin": 0, "aria-valuemax": max, "aria-label": label },
el("s", { style: `width:${max ? (value / max) * 100 : 0}%${color ? `;background:${color}` : ""}` }));

// Funnel — шаги воронки: rows [{ label, value, base, color }]; полоса — от max, % — от base (null — 100%)
const pct = (a, b) => (b ? Math.round((a / b) * 100) : 0);
function Funnel(rows, { max, note } = {}) {
  const top = Math.max(1, max ?? Math.max(...rows.map((r) => r.value)));
  return el("div", { className: "funnel" },
    rows.map((r) => el("div", { className: "f" },
      el("em", { textContent: r.label }),
      Meter(r.value, top, { color: r.color, label: r.label }),
      el("b", { textContent: r.value }),
      el("small", { textContent: r.base == null ? "100%" : `${pct(r.value, r.base)}%` }))),
    note ? el("div", { className: "muted fnote", textContent: note }) : null);
}

// Bars — столбцы с накоплением: items [{ total, parts: [{ value, tone: ok | accent }], title }] — снизу вверх;
// фон столбца — total, части — доля от total. from/to — подписи оси, legend [{ label, color }]
function Bars(items, { from, to, legend } = {}) {
  const top = Math.max(1, ...items.map((i) => i.total));
  return el("div", {},
    el("div", { className: cls("bars", items.length > 20 && "dense") }, items.map((i) => el("div", { style: `height:${(i.total / top) * 100}%`, title: i.title },
      (i.parts ?? []).map((p) => el("u", { className: p.tone === "ok" ? "ok" : "", style: `height:${i.total ? (p.value / i.total) * 100 : 0}%` }))))),
    from || to ? el("div", { className: "days" }, el("span", { textContent: from ?? "" }), el("span", { textContent: to ?? "" })) : null,
    legend ? el("div", { className: "legend" }, legend.map((l) => el("span", {}, el("i", { style: `background:${l.color}` }), l.label))) : null);
}

// RankList — строки «текст … число»: items [[text, n]]
const RankList = (items) => el("ul", { className: "reasons" }, items.map(([text, n]) => el("li", {}, el("span", { textContent: text }), el("b", { className: "mono", textContent: n }))));

// ── StackTable ────────────────────────────────────────────────────────
// Таблица, которая на телефоне становится карточками «поле: значение» (#120): подпись поля — из заголовка столбца.
// columns: [{ title, num? }], rows: массивы ячеек (текст или узлы)
function StackTable(columns, rows) {
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
function Field(label, control, { wide, stacked } = {}) {
  control.id ||= `f-${++fieldIds}`;
  if (stacked) return [el("label", { className: "field", for: control.id, textContent: label }), control];
  if (wide) control.classList.add("wide");
  return [el("label", { for: control.id, className: wide ? "wide" : "", textContent: label }), control];
}
const FormGrid = (...fields) => el("div", { className: "form" }, fields);

// Form — карточка-форма: onSubmit (может вернуть промис) держит submit занятой; className по умолчанию card
function Form({ submit, onSubmit, className = "card" } = {}, ...children) {
  const form = el("form", { className }, children);
  form.onsubmit = (e) => {
    e.preventDefault();
    return submit ? busy(submit, () => onSubmit(e)) : onSubmit(e);
  };
  return form;
}

// ── Toast, Sheet ──────────────────────────────────────────────────────
// Toast — короткий итог внизу экрана; undo — «Отменить» (вызывается один раз и закрывает тост); tone: ok | bad
function Toast(text, { undo, tone, timeout = 6000 } = {}) {
  let box = document.querySelector(".toasts");
  if (!box) document.body.append(box = el("div", { className: "toasts", role: "status", "aria-live": "polite" }));
  const close = () => toast.remove();
  const toast = el("div", { className: cls("toast", tone) }, el("span", { textContent: text }),
    undo ? Button("Отменить", { kind: "ghost", size: "sm", icon: "undo", onClick: async () => { close(); await undo(); } }) : null,
    Button("Закрыть", { kind: "ghost", size: "sm", icon: "x", iconOnly: true, onClick: close }));
  box.append(toast);
  if (timeout) setTimeout(close, timeout);
  return toast;
}

// Sheet — нижний лист: на телефоне — над нижней панелью, на компьютере — в углу. Закрывается кликом мимо и Esc.
// Узел с методами open() / close() / toggle(); title — подпись сверху
function Sheet(children, { title, label } = {}) {
  const sheet = el("div", { className: "sheet", hidden: true, role: "dialog", "aria-label": label ?? title }, title ? Caption(title) : null, children);
  sheet.open = () => { sheet.hidden = false; };
  sheet.close = () => { sheet.hidden = true; };
  sheet.toggle = () => { sheet.hidden = !sheet.hidden; };
  document.addEventListener("click", (e) => { if (!sheet.hidden && !sheet.contains(e.target)) sheet.close(); });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") sheet.close(); });
  return sheet;
}
// Пункты листа-меню: items [{ href, label, icon, active }]
const SheetLinks = (items) => items.map((i) => el("a", { href: i.href, className: i.active ? "active" : "" }, i.icon ? Icon(i.icon) : null, i.label));
