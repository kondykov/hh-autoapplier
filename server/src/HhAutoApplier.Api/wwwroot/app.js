"use strict";
// Единый веб-интерфейс (#46): разделы по хешу адреса поверх того же API, что у расширения. Вход — пароль, дальше cookie.
// Администратор может смотреть данные другого пользователя (?userId=) — только чтение: правки API делает от своего имени

const view = document.getElementById("view");
const status = document.getElementById("status");
const loginForm = document.getElementById("login");
const nav = document.getElementById("nav");
const viewAs = document.getElementById("view-as");

let me = null;
// Чьи данные показываем: null — свои
let viewedUser = null;

class Unauthorized extends Error {}

function setStatus(text, error = false) {
  status.textContent = text;
  status.className = error ? "error" : "";
}

async function api(path, options = {}) {
  const res = await fetch(path, { ...options, headers: { "Content-Type": "application/json" } });
  const body = res.status === 204 ? null : await res.json().catch(() => null);
  if (res.status === 401) throw new Unauthorized(body?.message ?? "нужен вход");
  if (res.status === 403) throw new Error(body?.message ?? "нет прав");
  // Ошибки проверки полей (400) — списком по полям
  const fields = body?.errors ? Object.values(body.errors).flat().join("; ") : null;
  if (!res.ok) throw new Error(body?.message ?? fields ?? body?.title ?? `сервер ответил ${res.status}`);
  return body;
}

const post = (path, body) => api(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });
const put = (path, body) => api(path, { method: "PUT", body: JSON.stringify(body) });

// Чтение с учётом выбранного пользователя
function scoped(path) {
  if (!viewedUser) return path;
  return `${path}${path.includes("?") ? "&" : "?"}userId=${encodeURIComponent(viewedUser)}`;
}

// Данные — только текстом: вакансии и письма приходят с hh.ru и от LLM
function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key.startsWith("data-") || key.startsWith("aria-") || key === "for" || key === "role") node.setAttribute(key, value);
    else node[key] = value;
  }
  for (const child of children.flat()) if (child != null && child !== false) node.append(child);
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

// Таблица, которая на телефоне становится карточками «поле: значение» (#120): подпись поля — из заголовка столбца.
// columns: [{ title, num? }], rows: массивы ячеек (текст или узлы)
function stackTable(columns, rows) {
  return el("table", { className: "stack" },
    el("thead", {}, el("tr", {}, columns.map((c) => el("th", { className: c.num ? "n" : "", textContent: c.title })))),
    el("tbody", {}, rows.map((cells) => el("tr", {}, cells.map((cell, i) => {
      const td = el("td", { className: columns[i]?.num ? "n" : "", "data-label": columns[i]?.title ?? "" });
      if (cell != null && cell !== false) td.append(cell);
      return td;
    })))));
}

// Источник вакансии (задел под другие агрегаторы, #32): пока все вакансии — с hh.ru
const SOURCES = { hh: { name: "hh.ru", color: "#ff6b78" } };
const sourcePill = (v) => {
  const s = SOURCES[v.source ?? "hh"] ?? { name: v.source, color: "#8b98a8" };
  return el("span", { className: "pill src", title: "Источник вакансии" }, el("i", { style: `background:${s.color}` }), s.name);
};

const date = (value) => (value ? new Date(value).toLocaleString("ru-RU", { dateStyle: "short", timeStyle: "short" }) : "");
const readOnly = () => viewedUser !== null;

const STATUSES = {
  New: "новая", Pending: "ждёт оценки", Review: "на проверке", Questionnaire: "анкета", Queued: "в очереди",
  Applied: "отклик отправлен", Invited: "приглашение", Rejected: "отказ", Skipped: "пропущена", Failed: "ошибка",
};

// Действие с кнопкой: на время запроса кнопка выключена, итог — в строке статуса
async function run(button, action, done) {
  button.disabled = true;
  try {
    const result = await action();
    if (done) setStatus(typeof done === "function" ? done(result) : done);
    return result;
  } catch (e) {
    if (e instanceof Unauthorized) showLogin();
    setStatus(e.message, true);
  } finally {
    button.disabled = false;
  }
}

// ── Разделы ──────────────────────────────────────────────────────────

// Дашборд (#121), демо: считается в браузере по тем же данным, что «Вакансии» (история статусов каждого отклика).
// В задаче — эндпоинт GET /api/dashboard: тогда страница перестанет выкачивать все отклики
const DAY = 86400000;
const PERIODS = [7, 30, 90];
const AGGREGATORS = [
  { id: "all", name: "Все" },
  { id: "hh", name: "hh.ru", color: "#ff6b78" },
  { id: "hirify", name: "hirify", color: "#8fb4ff", off: true },
  { id: "getmatch", name: "getmatch", color: "#b9a3ff", off: true },
];
const dashState = { period: 30, source: "hh" };

async function allApplications() {
  const items = [];
  for (let skip = 0; ; skip += 500) {
    const page = await api(scoped(`/api/vacancies?skip=${skip}&take=500&includeHidden=true`));
    items.push(...page);
    if (page.length < 500 || skip > 20000) return items;
  }
}

// Когда отклик впервые пришёл в статус (по истории); null — не приходил
function reached(v, status) {
  const h = (v.history ?? []).find((x) => x.to === status);
  if (h) return new Date(h.at).getTime();
  if (status === "Applied" && v.appliedAt) return new Date(v.appliedAt).getTime();
  return v.status === status && v.updatedAt ? new Date(v.updatedAt).getTime() : null;
}
const collected = (v) => new Date(v.collectedAt ?? v.history?.[0]?.at ?? 0).getTime();
const pct = (a, b) => (b ? Math.round((a / b) * 100) : 0);

async function dashboard() {
  const [apps, searchList, chats, collect] = await Promise.all([
    allApplications(),
    api(scoped("/api/searches")).catch(() => []),
    api(scoped("/api/dialogs?take=200")).catch(() => []),
    api("/api/collect/status").catch(() => null),
  ]);
  const root = el("div");

  const lastChat = chats.reduce((max, m) => Math.max(max, new Date(m.at).getTime() || 0), 0);
  const lastCollect = collect?.current ? "идёт сбор" : collect?.last?.finishedAt ? `сбор ${date(collect.last.finishedAt)}` : null;

  function draw() {
    const now = Date.now();
    const from = now - dashState.period * DAY;
    const prevFrom = from - dashState.period * DAY;
    // Источник: пока у всех откликов — hh.ru; «Все» = сумма источников
    const pool = apps.filter((v) => dashState.source === "all" || (v.source ?? "hh") === dashState.source);
    const inPeriod = (t, a = from, b = now) => t != null && t >= a && t < b;

    const cohort = pool.filter((v) => inPeriod(collected(v)));
    const prevCohort = pool.filter((v) => inPeriod(collected(v), prevFrom, from));
    const appliedIn = (a, b) => pool.filter((v) => inPeriod(reached(v, "Applied"), a, b));
    const applied = appliedIn(from, now);
    const prevApplied = appliedIn(prevFrom, from);
    const invited = pool.filter((v) => inPeriod(reached(v, "Invited")));
    const rejected = pool.filter((v) => inPeriod(reached(v, "Rejected")));

    const delta = (a, b) => {
      if (!b) return el("i", { textContent: a ? "новое за период" : "—" });
      const d = Math.round(((a - b) / b) * 100);
      return el("i", { className: d > 0 ? "up" : d < 0 ? "down" : "", textContent: `${d > 0 ? "+" : ""}${d}% к прошлым ${dashState.period} дн` });
    };
    const kpi = (value, label, note) => el("div", { className: "card kpi" }, el("b", {}, value), el("span", { textContent: label }), note);

    // Воронка — по вакансиям, собранным за период: % — от предыдущего шага, отказ — от отправленных
    const step = (pred) => cohort.filter(pred).length;
    const f = [
      ["Собрано", cohort.length],
      ["Оценено LLM", step((v) => v.llmScore != null)],
      ["В очередь", step((v) => reached(v, "Queued") != null || reached(v, "Applied") != null)],
      ["Отправлено", step((v) => reached(v, "Applied") != null)],
      ["Приглашение", step((v) => reached(v, "Invited") != null), "ok"],
    ];
    const sent = f[3][1];
    const rejectedCohort = step((v) => reached(v, "Rejected") != null);
    const max = Math.max(1, cohort.length);
    const funnelRow = (label, n, base, color) => el("div", { className: "f" },
      el("em", { textContent: label }),
      el("div", { className: "track" }, el("s", { style: `width:${(n / max) * 100}%${color ? `;background:${color}` : ""}` })),
      el("b", { textContent: n }), el("small", { textContent: base == null ? "100%" : `${pct(n, base)}%` }));

    // Отклики по дням (за 90 дней — по неделям): отправлено, ответы (приглашение или отказ), из них приглашения
    const bucket = dashState.period > 30 ? 7 : 1;
    const buckets = Math.ceil(dashState.period / bucket);
    const start = new Date(); start.setHours(0, 0, 0, 0);
    const first = start.getTime() - (buckets * bucket - 1) * DAY;
    const series = Array.from({ length: buckets }, (_, i) => ({ at: first + i * bucket * DAY, sent: 0, answered: 0, invited: 0 }));
    const add = (t, key) => {
      if (t == null || t < first) return;
      const i = Math.floor((t - first) / (bucket * DAY));
      if (series[i]) series[i][key]++;
    };
    for (const v of pool) {
      add(reached(v, "Applied"), "sent");
      add(reached(v, "Invited"), "invited");
      add(reached(v, "Invited"), "answered");
      add(reached(v, "Rejected"), "answered");
    }
    const top = Math.max(1, ...series.map((s) => Math.max(s.sent, s.answered)));
    const dayLabel = (t) => new Date(t).toLocaleDateString("ru-RU", { day: "numeric", month: "short" });
    const bars = el("div", { className: "bars", style: buckets > 20 ? "gap:2px" : "" }, series.map((s) => el("div", {
      style: `height:${(Math.max(s.sent, s.answered) / top) * 100}%`,
      title: `${dayLabel(s.at)}${bucket > 1 ? " (неделя)" : ""}: отправлено ${s.sent}, ответов ${s.answered}, приглашений ${s.invited}`,
    }, el("u", { className: "inv", style: `height:${s.answered ? (s.invited / Math.max(s.sent, s.answered)) * 100 : 0}%` }),
    el("u", { style: `height:${s.answered ? ((s.answered - s.invited) / Math.max(s.sent, s.answered)) * 100 : 0}%` }))));

    // По поискам: собрано за период, отправлено, ответили, приглашения, конверсия в приглашение
    const bySearch = new Map();
    for (const v of cohort) {
      const key = v.searchName ?? "без поиска";
      const row = bySearch.get(key) ?? { collected: 0, sent: 0, answered: 0, invited: 0 };
      row.collected++;
      if (reached(v, "Applied") != null) row.sent++;
      if (reached(v, "Invited") != null) row.invited++;
      if (reached(v, "Invited") != null || reached(v, "Rejected") != null) row.answered++;
      bySearch.set(key, row);
    }
    const searchRows = [...bySearch.entries()].sort((a, b) => b[1].collected - a[1].collected)
      .map(([name, r]) => [name, r.collected, r.sent, r.answered, r.invited, el("span", { style: r.invited ? "color:var(--ok)" : "", textContent: `${pct(r.invited, r.sent)}%` })]);

    // Причины отсева — по первой части причины («правило: …», «LLM: …»); числа и слова в кавычках — общим местом,
    // чтобы «оценка 59 ниже 70» и «оценка 62 ниже 70» легли в одну строку
    const reasons = new Map();
    for (const v of cohort.filter((x) => x.status === "Skipped")) {
      const text = (v.statusReason ?? "без причины").split(/[:.(]/)[0].replace(/«[^»]*»/g, "«…»").replace(/\d+/g, "N").trim().slice(0, 70) || "без причины";
      reasons.set(text, (reasons.get(text) ?? 0) + 1);
    }
    const topReasons = [...reasons.entries()].sort((a, b) => b[1] - a[1]).slice(0, 6);

    const periodSeg = el("div", { className: "seg", role: "group", "aria-label": "Период" }, PERIODS.map((p) => {
      const b = el("button", { type: "button", className: p === dashState.period ? "on" : "", textContent: `${p} дн` });
      b.onclick = () => { dashState.period = p; draw(); };
      return b;
    }));
    const sources = el("div", { className: "sources" }, el("span", { className: "muted", style: "font-size:12px", textContent: "Агрегатор" }),
      AGGREGATORS.map((a) => {
        const b = el("button", { type: "button", className: a.id === dashState.source ? "on" : "", disabled: !!a.off,
          title: a.off ? "После подключения (#32)" : "" }, a.color ? el("i", { style: `background:${a.color}` }) : null, a.name);
        b.onclick = () => { dashState.source = a.id; draw(); };
        return b;
      }));
    const sourceName = AGGREGATORS.find((a) => a.id === dashState.source).name;

    root.replaceChildren(
      el("div", { className: "dash-head" },
        el("div", {}, el("h1", { textContent: "Дашборд" }),
          el("div", { className: "muted", style: "font-size:13px", textContent: [
            lastChat ? `последнее сообщение из чатов ${date(lastChat)}` : "чаты ещё не синхронизировались",
            lastCollect,
          ].filter(Boolean).join(" · ") })),
        periodSeg),
      sources,
      el("div", { className: "demo-note", textContent: "Демо #121: числа считаются в браузере по истории откликов. Ответы работодателей — на момент последней синхронизации чатов в расширении." }),
      el("div", { className: "kpis" },
        kpi(cohort.length, "собрано вакансий", delta(cohort.length, prevCohort.length)),
        kpi(applied.length, "откликов отправлено", delta(applied.length, prevApplied.length)),
        kpi([String(pct(invited.length, applied.length)), el("span", { className: "muted", style: "font-size:16px", textContent: "%" })], "в приглашение",
          el("i", { textContent: `${invited.length} приглашений за период` })),
        kpi(rejected.length, "отказов", el("i", { textContent: "собеседования — после #122" }))),
      el("div", { className: "cols" },
        el("div", { className: "card funnel" }, el("h3", { textContent: `Воронка за ${dashState.period} дней · ${sourceName}` }),
          f.map(([label, n, color], i) => funnelRow(label, n, i ? f[i - 1][1] : null, color ? "var(--ok)" : null)),
          funnelRow("Отказ", rejectedCohort, sent, "var(--bad)"),
          el("div", { className: "muted", style: "font-size:11px;margin-top:8px", textContent: "% — от предыдущего шага; отказ — от отправленных" })),
        el("div", { className: "card" }, el("h3", { textContent: bucket > 1 ? "Отклики по неделям" : "Отклики по дням" }), bars,
          el("div", { className: "days" }, el("span", { textContent: dayLabel(first) }), el("span", { textContent: "сегодня" })),
          el("div", { className: "legend" },
            el("span", {}, el("i", { style: "background:var(--bar)" }), "отправлено"),
            el("span", {}, el("i", { style: "background:var(--accent)" }), "ответили отказом"),
            el("span", {}, el("i", { style: "background:var(--ok)" }), "приглашение")))),
      el("div", { className: "cols wide-left", style: "margin-top:12px" },
        el("div", { className: "card" }, el("h3", { textContent: `По поискам · ${sourceName}` }),
          searchRows.length ? stackTable([{ title: "Поиск" }, { title: "Собрано", num: true }, { title: "Отправлено", num: true }, { title: "Ответили", num: true }, { title: "Приглашения", num: true }, { title: "Конверсия", num: true }], searchRows)
            : el("p", { className: "empty", textContent: "За период ничего не собрано." }),
          el("p", { className: "muted", style: "font-size:12px;margin:8px 0 0", textContent: `Поисков включено: ${searchList.filter((s) => s.enabled).length} из ${searchList.length}` })),
        el("div", { className: "card" }, el("h3", { textContent: "Почему отсеяно" }),
          topReasons.length ? el("ul", { className: "reasons" }, topReasons.map(([text, n]) => el("li", {}, el("span", { textContent: text }), el("b", { className: "mono", textContent: n }))))
            : el("p", { className: "empty", textContent: "Отсеянных за период нет." }))));
  }

  draw();
  return root;
}

async function overview() {
  const [stats, telegram] = await Promise.all([api(scoped("/api/stats")), readOnly() ? null : api("/api/telegram").catch(() => null)]);
  const order = ["Queued", "Review", "Questionnaire", "Pending", "Applied", "Invited", "Rejected", "Skipped", "Failed", "New"];
  const total = Object.values(stats).reduce((a, b) => a + b, 0);
  const tiles = order.filter((s) => stats[s]).map((s) =>
    el("a", { className: "tile", href: `#vacancies/${s}`, style: "text-decoration:none;color:inherit" }, el("b", { textContent: stats[s] }), el("span", { textContent: STATUSES[s] })));

  return [
    el("h1", { textContent: "Обзор" }),
    el("p", { className: "sub", textContent: `Всего откликов в работе: ${total}. Нажмите на счётчик — откроется список.` }),
    tiles.length ? el("div", { className: "grid" }, tiles) : el("p", { className: "empty", textContent: "Откликов пока нет: добавьте поиск — сервер соберёт вакансии." }),
    el("div", { className: "card" },
      el("h2", { textContent: "Что дальше" }),
      el("ul", {},
        stats.Review ? el("li", {}, `На проверке ${stats.Review}: `, el("a", { href: "#review", textContent: "решить" })) : null,
        stats.Failed ? el("li", {}, `С ошибкой ${stats.Failed}: `, el("a", { href: "#vacancies/Failed", textContent: "вернуть в очередь или оставить" })) : null,
        stats.Skipped ? el("li", {}, `Пропущено ${stats.Skipped} (LLM, правила или вы): `, el("a", { href: "#vacancies/Skipped", textContent: "пересмотреть и вернуть нужные в очередь" })) : null,
        stats.Queued ? el("li", {}, `В очереди ${stats.Queued}: `, el("a", { href: "#vacancies/Queued", textContent: "лишние можно пропустить" })) : null,
        el("li", {}, "Ответы работодателей — ", el("a", { href: "#dialogs", textContent: "приглашения и вопросы с черновиками" })),
        el("li", {}, "Отчёт по откликам — ", el("a", { href: scoped("/api/report?format=csv"), textContent: "CSV" })),
        telegram?.configured && !readOnly() ? el("li", {}, telegram.linked ? `Telegram привязан${telegram.username ? ` (@${telegram.username})` : ""}` : "Telegram не привязан — ",
          telegram.linked ? null : el("a", { href: "#account", textContent: "привязать в профиле" })) : null)),
  ];
}

async function vacancies(statusFilter) {
  const take = 50;
  const searches = await api(scoped("/api/searches"));
  const state = { status: statusFilter ?? "", search: "", hidden: false, skip: 0 };

  const statusSelect = el("select", { "aria-label": "Статус" }, el("option", { value: "", textContent: "Все статусы" }),
    Object.entries(STATUSES).map(([value, text]) => el("option", { value, textContent: text })));
  statusSelect.value = state.status;
  const searchSelect = el("select", { "aria-label": "Поиск" }, el("option", { value: "", textContent: "Все поиски" }),
    searches.map((s) => el("option", { value: s.name, textContent: s.name })));
  const hidden = el("input", { type: "checkbox" });
  const prev = el("button", { textContent: "← Назад" });
  const next = el("button", { textContent: "Дальше →" });
  const page = el("span", { className: "muted" });
  const list = el("div");

  async function load() {
    const query = new URLSearchParams({ skip: state.skip, take });
    if (state.status) query.set("status", state.status);
    if (state.search) query.set("search", state.search);
    if (state.hidden) query.set("includeHidden", "true");
    try {
      const items = await api(scoped(`/api/vacancies?${query}`));
      list.replaceChildren(...(items.length ? items.map(vacancyCard) : [el("p", { className: "empty", textContent: "Ничего нет." })]));
      prev.disabled = state.skip === 0;
      next.disabled = items.length < take;
      page.textContent = items.length ? `${state.skip + 1}–${state.skip + items.length}` : "";
    } catch (e) {
      setStatus(e.message, true);
    }
  }

  statusSelect.onchange = () => { state.status = statusSelect.value; state.skip = 0; load(); };
  searchSelect.onchange = () => { state.search = searchSelect.value; state.skip = 0; load(); };
  hidden.onchange = () => { state.hidden = hidden.checked; state.skip = 0; load(); };
  prev.onclick = () => { state.skip = Math.max(0, state.skip - take); load(); };
  next.onclick = () => { state.skip += take; load(); };
  await load();

  return [el("h1", { textContent: "Вакансии" }), el("div", { className: "bar" }, statusSelect, searchSelect, el("label", {}, hidden, " Показать скрытые"), prev, next, page), list];
}

function salary(v) {
  if (v.salaryFrom == null && v.salaryTo == null) return null;
  const range = [v.salaryFrom != null ? `от ${v.salaryFrom.toLocaleString("ru-RU")}` : "", v.salaryTo != null ? `до ${v.salaryTo.toLocaleString("ru-RU")}` : ""];
  return `${range.filter(Boolean).join(" ")} ${v.salaryCurrency ?? ""}`.trim();
}

// Решения по вакансии вне ручной проверки: пропущенную или упавшую — снова в очередь, ожидающую — пропустить (забыть)
const REQUEUE = ["Skipped", "Failed"];
const SKIPPABLE = ["Queued", "Pending", "Questionnaire", "New"];

function vacancyActions(v, pill) {
  if (readOnly()) return null;
  if (v.status === "Review") return el("div", { className: "actions" }, el("a", { href: "#review", textContent: "Решить в «Проверке» →" }));

  const result = el("span", { className: "result" });
  const done = (node, status) => {
    node.closest("article").classList.add("decided");
    pill.textContent = STATUSES[status];
    return `«${v.title}»: ${STATUSES[status]}`;
  };

  if (v.hiddenAt) {
    const unhide = el("button", { textContent: "Вернуть" });
    unhide.onclick = () => run(unhide, () => post(`/api/vacancies/${encodeURIComponent(v.id)}/unhide`), () => {
      unhide.remove();
      result.textContent = "возвращена в список";
      result.closest("article").classList.add("decided");
      return `«${v.title}»: возвращена в список`;
    });
    return el("div", { className: "actions" }, unhide, result);
  }

  if (REQUEUE.includes(v.status)) {
    const requeue = el("button", { className: "primary", textContent: "В очередь" });
    // Оценить заново и скрыть — только отсеянные: у Failed отклик мог уйти на hh.ru
    const reevaluate = v.dismissed ? el("button", { textContent: "Оценить заново" }) : null;
    const hide = v.dismissed ? el("button", { textContent: "Скрыть" }) : null;
    const clear = () => [requeue, reevaluate, hide].forEach((b) => b?.remove());

    requeue.onclick = () => run(requeue, () => post(`/api/vacancies/${encodeURIComponent(v.id)}/status`, { status: "Queued" }),
      () => { clear(); result.textContent = "в очереди"; return done(result, "Queued"); });
    if (v.dismissed) {
      reevaluate.onclick = () => run(reevaluate, () => post(`/api/vacancies/${encodeURIComponent(v.id)}/reevaluate`), (r) => {
        clear();
        result.textContent = r.status === "Skipped" ? `снова отсеяна: ${r.statusReason ?? ""}` : STATUSES[r.status];
        return done(result, r.status);
      });
      hide.onclick = () => run(hide, () => post(`/api/vacancies/${encodeURIComponent(v.id)}/hide`), () => {
        clear();
        result.textContent = "скрыта";
        result.closest("article").classList.add("decided");
        return `«${v.title}»: скрыта`;
      });
    }
    return el("div", { className: "actions" }, requeue, reevaluate, hide, result);
  }

  if (SKIPPABLE.includes(v.status)) {
    const reason = el("input", { placeholder: "Причина (необязательно)", "aria-label": "Причина пропуска" });
    const skip = el("button", { className: "danger", textContent: "Пропустить" });
    skip.onclick = () => run(skip, () => post(`/api/vacancies/${encodeURIComponent(v.id)}/status`,
      { status: "Skipped", reason: reason.value.trim() ? `пропущено вручную: ${reason.value.trim()}` : "пропущено вручную" }),
    () => { skip.remove(); reason.remove(); result.textContent = "пропущена"; return done(result, "Skipped"); });
    return el("div", { className: "actions" }, reason, skip, result);
  }
  return null;
}

// Просьбы работодателя к письму (#72). Цитата — только для чтения: в письмо идёт лишь проверенное значение
const LETTER_REQUESTS = { Arithmetic: "Пример", Salary: "Зарплата", TestTask: "Тестовое", CodeWord: "Кодовое слово", Other: "Прочее" };

function letterRequestLine(r) {
  const state = r.closed ? (r.answer ? `закрыта: ${r.answer}` : "решено человеком") : (r.problem ?? `ответ: ${r.answer} — допишется к письму`);
  return el("div", { className: "reason" }, el("b", { textContent: LETTER_REQUESTS[r.type] ?? r.type }), r.text ? ` — ${r.text}` : "", el("br"), state);
}

function letterRequestsBlock(v) {
  if (v.letterRequestsState === "Failed") return el("div", { className: "reason", textContent: "Просьбы работодателя к письму разобрать не удалось: прочитайте вакансию и напишите письмо сами." });
  return v.letterRequests?.length ? el("details", { open: v.letterRequestsState === "Found" }, el("summary", { textContent: `Просьбы к письму (${v.letterRequests.length})` }), v.letterRequests.map(letterRequestLine)) : null;
}

function vacancyCard(v) {
  const pill = el("span", { className: "pill", textContent: STATUSES[v.status] ?? v.status });
  return el("article", { className: "card" },
    el("div", {}, el("a", { href: safeUrl(v.url), target: "_blank", rel: "noopener", textContent: v.title, title: "Открыть вакансию" }), " ", pill, " ", sourcePill(v),
      v.hiddenAt ? " " : null, v.hiddenAt ? el("span", { className: "pill", textContent: "скрыта" }) : null,
      v.vacancyArchived ? " " : null, v.vacancyArchived ? el("span", { className: "pill", textContent: "в архиве" }) : null),
    el("div", { className: "meta", textContent: [v.company, v.area, salary(v), v.workFormat, v.workExperience && `опыт: ${v.workExperience}`, `балл ${v.llmScore ?? v.score}`, v.resumeProfile && `резюме ${v.resumeProfile}`, v.searchName && `поиск ${v.searchName}`, v.appliedAt && `отклик ${date(v.appliedAt)}`].filter(Boolean).join(" · ") }),
    // Вакансия общая: даты — когда её нашли впервые и когда она встретилась в сборе или импорте последний раз (#70)
    el("div", { className: "meta", textContent: [v.collectedAt && `найдена ${date(v.collectedAt)}`, v.lastSeenAt && `встречалась ${date(v.lastSeenAt)}`].filter(Boolean).join(" · ") }),
    v.statusReason || v.llmReason ? el("div", { className: "reason", textContent: v.statusReason ?? v.llmReason }) : null,
    letterRequestsBlock(v),
    v.coverLetter ? el("details", {}, el("summary", { textContent: "Письмо" }), el("div", { className: "pre", textContent: v.coverLetter })) : null,
    v.history?.length ? el("details", {}, el("summary", { textContent: `История (${v.history.length})` }),
      stackTable([{ title: "Когда" }, { title: "Статус" }, { title: "Причина" }],
        v.history.map((h) => [date(h.at), STATUSES[h.to] ?? h.to, h.reason ?? ""]))) : null,
    vacancyActions(v, pill));
}

// Ручная проверка (#25, #58): вакансии, в которых LLM сомневается, и анкеты с текстовыми ответами LLM.
// «Одобрить» — в очередь с письмом и ответами из полей, «Отклонить» — пропуск с причиной
async function review() {
  const items = (await api(scoped("/api/vacancies?status=Review&take=200"))).sort((a, b) => b.score - a.score);
  const list = el("div", {}, items.length ? items.map(reviewCard) : el("p", { className: "empty", textContent: "На проверке ничего нет." }));
  return [
    el("h1", { textContent: "Проверка" }),
    el("p", { className: "sub", textContent: `Вакансии, в которых LLM сомневается, и анкеты с ответами LLM: ${items.length}. Письмо и текстовые ответы можно поправить перед одобрением.` }),
    list,
  ];
}

// Поле ответа на вопрос анкеты: label и node — для карточки, inputs — что выключать на время запроса, value() — ответ для API
function answerField(v, q, i) {
  const id = `answer-${v.id}-${i}`;
  const label = el("label", { className: "field", for: id, textContent: q.question });
  if (q.type === "Text") {
    const input = el("textarea", { value: q.answer.join("\n"), id, disabled: readOnly(), "aria-label": q.question });
    return { label, node: input, inputs: [input], value: () => [input.value.trim()] };
  }
  if (q.type === "Radio") {
    const select = el("select", { id, disabled: readOnly(), "aria-label": q.question },
      el("option", { value: "", textContent: "— не выбрано —" }), q.options.map((o) => el("option", { value: o, textContent: o })));
    select.value = q.options.includes(q.answer[0]) ? q.answer[0] : "";
    return { label, node: select, inputs: [select], value: () => (select.value ? [select.value] : []) };
  }
  if (q.type === "Checkbox") {
    const boxes = q.options.map((o, k) => el("input", { type: "checkbox", id: `${id}-${k}`, value: o, checked: q.answer.includes(o), disabled: readOnly() }));
    const node = el("div", { role: "group", "aria-label": q.question },
      boxes.map((b) => el("div", {}, b, " ", el("label", { for: b.id, textContent: b.value }))));
    return { label: el("b", { textContent: q.question }), node, inputs: boxes, value: () => boxes.filter((b) => b.checked).map((b) => b.value) };
  }
  return null;
}

// Ответ поля совпадает с ответом LLM: текст — без пробелов по краям, варианты — тем же набором
const sameAnswer = (value, q) => q.type === "Text"
  ? (value[0] ?? "") === q.answer.join("\n").trim()
  : value.length === q.answer.length && value.every((x) => q.answer.includes(x));

function reviewCard(v) {
  const awaitsLetter = v.letterRequestsState === "Found" || v.letterRequestsState === "Failed";
  const letter = el("textarea", { value: v.coverLetter ?? "", id: `letter-${v.id}`, disabled: readOnly(),
    placeholder: awaitsLetter ? "LLM ещё не написала письмо. Без письма одобрить нельзя: шаблон расширения не ответит на просьбы работодателя." : "LLM ещё не написала письмо. Напишите своё или одобрите без письма — расширение возьмёт шаблон." });
  const reason = el("input", { placeholder: "Причина отказа (необязательно)", "aria-label": "Причина отказа" });
  const approve = el("button", { className: "approve", textContent: "Одобрить" });
  const decline = el("button", { className: "danger", textContent: "Отклонить" });
  const result = el("span", { className: "result" });
  // Ответы LLM на анкету (#58) — в полях: уходят работодателю от имени кандидата. Правится каждый вопрос (#115):
  // текст — полем, один вариант — списком, несколько — галочками. Нераспознанный вопрос (Unknown) — только показ
  const questions = v.questions ?? [];
  const answered = questions.some((q) => q.answer.length);
  const answerFields = questions.map((q, i) => (answered ? answerField(v, q, i) : null));
  const fields = () => [letter, ...answerFields.filter(Boolean).flatMap((f) => f.inputs)];

  // Кодовое слово вставляется только по подтверждению, значения из профиля — по кнопке после его заполнения. Ссылки и контакты не выполняются
  const resolveRequests = (button, confirmCodeWords) => run(button, () => post(`/api/vacancies/${encodeURIComponent(v.id)}/letter-requests/resolve`, { confirmCodeWords }),
    (updated) => { node.replaceWith(reviewCard(updated)); return `«${v.title}»: письмо обновлено`; });
  const requestRows = (v.letterRequests ?? []).map((r, i) => {
    if (readOnly() || r.closed || r.type !== "CodeWord" || !r.value) return letterRequestLine(r);
    const confirm = el("button", { type: "button", textContent: `Вставить слово «${r.value}»` });
    confirm.onclick = () => resolveRequests(confirm, [i]);
    return el("div", {}, letterRequestLine(r), el("div", { className: "actions" }, confirm));
  });
  const needsProfile = (v.letterRequests ?? []).some((r) => !r.closed && (r.type === "Salary" || r.type === "TestTask"));
  const refresh = needsProfile && !readOnly() && v.letterRequestsState === "Found" ? el("button", { type: "button", textContent: "Подставить из профиля" }) : null;
  if (refresh) refresh.onclick = () => resolveRequests(refresh, []);

  const node = el("article", { className: "card" },
    el("h2", {}, el("a", { href: safeUrl(v.url), target: "_blank", rel: "noopener", textContent: v.title })),
    el("div", { className: "meta", textContent: [v.company, v.area, salary(v) ?? "зарплата не указана", v.workFormat, v.workExperience && `опыт: ${v.workExperience}`, `балл ${v.score}`, v.resumeProfile && `резюме ${v.resumeProfile}`, v.hasTest && "есть тест"].filter(Boolean).join(" · ") }),
    el("div", { className: "verdict", textContent: v.statusReason ?? v.llmReason ?? "LLM сомневается" }),
    v.stack?.length ? el("div", { className: "meta", textContent: `Стек: ${v.stack.join(", ")}` }) : null,
    el("details", {}, el("summary", { textContent: "Описание вакансии" }), el("div", { className: "desc", textContent: v.description || "описания нет" })),
    questions.length ? el("details", { open: answered }, el("summary", { textContent: `Анкета (${questions.length})` }),
      questions.map((q, i) => answerFields[i]
        ? el("div", { className: "reason" }, answerFields[i].label, answerFields[i].node)
        : el("div", { className: "reason" }, el("b", { textContent: q.question }), el("br"), q.answer.length ? q.answer.join("; ") : "ответа нет"))) : null,
    v.letterRequestsState === "Failed" ? letterRequestsBlock(v) : null,
    requestRows.length ? el("details", { open: true }, el("summary", { textContent: `Просьбы к письму (${requestRows.length})` }), requestRows, refresh ? el("div", { className: "actions" }, refresh) : null) : null,
    el("label", { className: "field", for: letter.id, textContent: v.coverLetterModel ? `Письмо (${v.coverLetterModel === "manual" ? "ваше" : v.coverLetterModel})` : "Письмо" }),
    letter,
    readOnly() ? null : el("div", { className: "actions" }, approve, reason, decline, result));

  // Карточка после решения остаётся на месте с итогом: если убрать её, следующая подъедет под курсор и лишний клик решит за неё
  async function act(body, done) {
    approve.disabled = decline.disabled = reason.disabled = true;
    fields().forEach((f) => (f.disabled = true));
    try {
      await post(`/api/vacancies/${encodeURIComponent(v.id)}/review`, body);
      node.classList.add("decided");
      result.textContent = done;
      setStatus(`«${v.title}»: ${done}. Без решения: ${view.querySelectorAll("article.card:not(.decided)").length}`);
    } catch (e) {
      if (e instanceof Unauthorized) showLogin();
      setStatus(`«${v.title}»: ${e.message}`, true);
      approve.disabled = decline.disabled = reason.disabled = false;
      fields().forEach((f) => (f.disabled = false));
    }
  }

  // Письмо уходит, только если его поправили: иначе остаётся письмо LLM с её моделью.
  // Ответы анкеты — все по порядку вопросов, если поправлен хоть один; вариант не из списка или пустой выбор сервер отклонит (409)
  approve.onclick = () => {
    const text = letter.value.trim();
    const edited = answerFields.some((f, i) => f && !sameAnswer(f.value(), questions[i]));
    const answers = edited ? questions.map((q, i) => (answerFields[i] ? answerFields[i].value() : q.answer)) : null;
    act({ approve: true, coverLetter: text && text !== (v.coverLetter ?? "").trim() ? text : null, answers }, "одобрено, в очереди");
  };
  decline.onclick = () => act({ approve: false, reason: reason.value.trim() || null }, "отклонено");
  return node;
}

// Ответы работодателей (#26): разбор LLM и черновик; отвечает человек — в чате hh.ru
const KINDS = { Invitation: "Приглашение", Question: "Вопрос", Rejection: "Отказ", Other: "Прочее" };
const AUTO_REPLY = { Pending: "автоответ ждёт отправки", Sent: "автоответ отправлен", Unavailable: "автоответ невозможен" };

async function dialogs(filter = "open") {
  const open = filter === "open";
  const items = (await api(scoped(`/api/dialogs?take=200${open ? "&handled=false" : ""}`))).filter((m) => !open || m.kind !== "Rejection");
  const select = el("select", { "aria-label": "Какие сообщения показать" },
    el("option", { value: "open", textContent: "Необработанные, кроме отказов" }), el("option", { value: "all", textContent: "Все" }));
  select.value = open ? "open" : "all";
  select.onchange = () => { location.hash = `#dialogs/${select.value}`; };

  return [
    el("h1", { textContent: "Ответы работодателей" }),
    el("p", { className: "sub", textContent: "Сообщения из чатов hh.ru присылает расширение («Автоответ» на странице чатов). LLM разбирает приглашения и вопросы и предлагает ответ — отправляете вы. Отказы распознаются без LLM." }),
    el("div", { className: "bar" }, select, el("span", { className: "muted", textContent: `Сообщений: ${items.length}` })),
    items.length ? items.map(dialogCard)
      : el("p", { className: "empty", textContent: open ? "Новых ответов работодателей нет." : "Сообщений пока нет: нажмите «Автоответ» в панели расширения на странице чатов hh.ru." }),
  ];
}

function dialogCard(m) {
  const chatUrl = `https://hh.ru/chat/${encodeURIComponent(m.chatId)}`;
  const rejection = m.kind === "Rejection";
  const reply = el("textarea", { value: m.suggestedReply ?? "", id: `reply-${m.chatId}`,
    placeholder: m.kind == null ? "LLM ещё не разобрала сообщение — обновите страницу через минуту." : "Черновика нет." });
  const copy = el("button", { className: "primary", textContent: "Копировать и открыть чат" });
  const done = el("button", { textContent: "Обработано" });
  const result = el("span", { className: "result" });

  const node = el("article", { className: m.handledAt ? "card decided" : "card" },
    el("h2", {}, el("a", { href: chatUrl, target: "_blank", rel: "noopener", textContent: m.title || "Чат без вакансии" })),
    el("div", { className: "meta" },
      el("span", { className: `kind ${m.kind ?? "pending"}`, textContent: KINDS[m.kind] ?? "ждёт разбора" }),
      [m.company, date(m.at), m.hhState && `hh: ${m.hhState}`, AUTO_REPLY[m.autoReply], m.autoReplyError, m.handledAt && "обработано"].filter(Boolean).join(" · ")),
    m.summary ? el("div", { className: "summary", textContent: m.summary }) : null,
    el("div", { className: "msg", textContent: m.text }),
    rejection ? null : el("label", { className: "field", for: reply.id,
      textContent: m.llmModel ? `Черновик ответа (${m.llmModel}) — поправьте [заглушки] перед отправкой` : "Черновик ответа" }),
    rejection ? null : reply,
    el("div", { className: "actions" }, rejection ? null : copy, m.handledAt || readOnly() ? null : done, result));

  // Буфер обмена доступен не везде (http без localhost): тогда текст просто выделяется для Ctrl+C
  copy.onclick = async () => {
    try {
      await navigator.clipboard.writeText(reply.value);
      result.textContent = "скопировано";
    } catch {
      reply.select();
      result.textContent = "выделено — скопируйте Ctrl+C";
    }
    window.open(chatUrl, "_blank", "noopener");
  };

  done.onclick = () => run(done, () => post(`/api/dialogs/${encodeURIComponent(m.chatId)}/handled`), () => {
    node.classList.add("decided");
    done.remove();
    result.textContent = "обработано";
    return `«${m.title}»: обработано`;
  });
  return node;
}

const lines = (text) => text.split(/[\n,]/).map((s) => s.trim()).filter(Boolean);

function field(label, input, wide = false) {
  input.id ||= `f-${Math.random().toString(36).slice(2)}`;
  return wide ? [el("label", { for: input.id, className: "wide", textContent: label }), Object.assign(input, { className: "wide" })]
    : [el("label", { for: input.id, textContent: label }), input];
}

async function searches() {
  const items = await api(scoped("/api/searches"));
  const list = items.map((s) => searchForm(s));
  if (!readOnly()) list.push(searchForm(null));
  return [
    el("h1", { textContent: "Поиски" }),
    el("p", { className: "sub", textContent: "Сервер собирает вакансии по включённым поискам раз в час и отбирает по правилам; остальное оценивает LLM." }),
    list,
  ];
}

function searchForm(s) {
  const r = s?.rules ?? {};
  const inputs = {
    name: el("input", { value: s?.name ?? "", required: true }),
    query: el("input", { value: s?.query ?? "", required: true }),
    area: el("input", { value: s?.area ?? "", placeholder: "113 — Россия, 1 — Москва" }),
    extraParams: el("input", { value: s?.extraParams ?? "", placeholder: "work_format=REMOTE&experience=between3And6" }),
    enabled: el("input", { type: "checkbox", checked: s?.enabled ?? true }),
    includeKeywords: el("input", { value: (r.includeKeywords ?? []).join(", ") }),
    excludeKeywords: el("input", { value: (r.excludeKeywords ?? []).join(", ") }),
    levelKeywords: el("input", { value: (r.levelKeywords ?? []).join(", "), placeholder: "junior, младший", title: "Ищутся только в названии. Отсев — только без опыта или при стажировке, иначе решает LLM" }),
    allowedFormats: el("input", { value: (r.allowedFormats ?? []).join(", "), placeholder: "Удалённо, Гибрид" }),
    minSalaryRub: el("input", { type: "number", value: r.minSalaryRub ?? "" }),
    minScore: el("input", { type: "number", value: r.minScore ?? 0 }),
    requireSalary: el("input", { type: "checkbox", checked: !!r.requireSalary }),
    skipWithTest: el("input", { type: "checkbox", checked: !!r.skipWithTest }),
    skipReview: el("input", { type: "checkbox", checked: !!r.skipReview }),
  };
  if (readOnly()) Object.values(inputs).forEach((i) => (i.disabled = true));

  const save = el("button", { className: "primary", textContent: s ? "Сохранить" : "Добавить" });
  const remove = s ? el("button", { className: "danger", textContent: "Удалить", type: "button" }) : null;
  const form = el("form", { className: "card" },
    el("h2", { textContent: s ? s.name : "Новый поиск" }),
    el("div", { className: "form" },
      field("Название", inputs.name), field("Запрос", inputs.query), field("Регион (area)", inputs.area), field("Доп. параметры", inputs.extraParams),
      field("Включён", inputs.enabled), field("Нужные слова", inputs.includeKeywords), field("Исключить слова", inputs.excludeKeywords), field("Слова уровня (в названии)", inputs.levelKeywords),
      field("Форматы работы", inputs.allowedFormats), field("Зарплата от, ₽", inputs.minSalaryRub), field("Мин. балл", inputs.minScore),
      field("Только с зарплатой", inputs.requireSalary), field("Пропускать с тестом", inputs.skipWithTest), field("Без ручной проверки", inputs.skipReview)),
    readOnly() ? null : el("div", { className: "bar", style: "margin:12px 0 0" }, save, remove));

  form.onsubmit = (e) => {
    e.preventDefault();
    run(save, () => put("/api/searches", {
      name: inputs.name.value.trim(), query: inputs.query.value.trim(), area: inputs.area.value.trim() || null,
      extraParams: inputs.extraParams.value.trim() || null, enabled: inputs.enabled.checked,
      rules: {
        includeKeywords: lines(inputs.includeKeywords.value), excludeKeywords: lines(inputs.excludeKeywords.value), levelKeywords: lines(inputs.levelKeywords.value),
        allowedFormats: lines(inputs.allowedFormats.value), minSalaryRub: inputs.minSalaryRub.value ? Number(inputs.minSalaryRub.value) : null,
        minScore: Number(inputs.minScore.value || 0), requireSalary: inputs.requireSalary.checked,
        skipWithTest: inputs.skipWithTest.checked, skipReview: inputs.skipReview.checked,
      },
    }), (saved) => { render(); return `Поиск «${saved.name}» сохранён`; });
  };
  if (remove) remove.onclick = () => {
    if (confirm(`Удалить поиск «${s.name}»?`))
      run(remove, () => api(`/api/searches/${s.id}`, { method: "DELETE" }), () => { render(); return `Поиск «${s.name}» удалён`; });
  };
  return form;
}

async function resumes() {
  const [items, profile] = await Promise.all([api(scoped("/api/resumes")), api(scoped("/api/profile"))]);
  const list = items.map((r) => resumeForm(r));
  if (!readOnly()) list.push(resumeForm(null));
  return [
    el("h1", { textContent: "Резюме" }),
    el("p", { className: "sub", textContent: "Профили резюме: «совпадение» — часть названия резюме на hh.ru, по ней расширение выбирает резюме. Описание и шаблон письма читает LLM." }),
    candidateForm(profile),
    list,
  ];
}

// Профиль кандидата (#35): общая часть и факты по навыкам — общие для всех резюме.
// Под вакансию LLM получает общую часть и факты только по навыкам этой вакансии
function candidateForm(profile) {
  const general = el("textarea", { value: profile.general ?? "", disabled: readOnly(),
    placeholder: "Уровень, стаж, формат работы, город, языки, пожелания" });
  // Для ответов на просьбы работодателя (#72): LLM их не получает, в письмо дописывает сервер
  const desiredSalary = el("input", { type: "number", min: "1", step: "1", value: profile.desiredSalaryRub ?? "", disabled: readOnly(), placeholder: "Например, 150000", "aria-label": "Желаемая зарплата на руки, ₽" });
  const readyForTest = el("select", { disabled: readOnly(), "aria-label": "Готовность к тестовому заданию" },
    el("option", { value: "", textContent: "не указано" }), el("option", { value: "true", textContent: "да" }), el("option", { value: "false", textContent: "нет" }));
  readyForTest.value = profile.readyForTestTask == null ? "" : String(profile.readyForTestTask);
  const rows = el("div");

  function skillRow(s = { skill: "", facts: "" }) {
    const skill = el("input", { value: s.skill, placeholder: "Навык, синонимы через «/»: EF Core / Entity Framework", "aria-label": "Навык", disabled: readOnly() });
    const facts = el("textarea", { value: s.facts, placeholder: "Где применял, сколько, что делал, цифры", "aria-label": "Факты", disabled: readOnly() });
    facts.style.minHeight = "70px";
    const remove = el("button", { type: "button", className: "danger", textContent: "Убрать" });
    const row = el("div", { className: "card", style: "padding:10px" }, skill, facts, readOnly() ? null : el("div", { className: "actions" }, remove));
    skill.style.width = "100%";
    skill.style.marginBottom = "6px";
    remove.onclick = () => row.remove();
    row.read = () => ({ skill: skill.value.trim(), facts: facts.value.trim() });
    return row;
  }

  rows.append(...profile.skills.map(skillRow));
  if (!readOnly() && profile.skills.length === 0) rows.append(skillRow());

  const add = el("button", { type: "button", textContent: "+ Навык" });
  add.onclick = () => rows.append(skillRow());
  const save = el("button", { className: "primary", textContent: "Сохранить профиль" });
  const form = el("form", { className: "card" },
    el("h2", { textContent: "Профиль кандидата" }),
    el("p", { className: "muted", textContent: "Общий для всех резюме. В письмо, оценку, ответы анкет и разбор ответов LLM получает общую часть и факты только по навыкам вакансии. Пишите только то, что было: это уходит работодателю от вашего имени." }),
    el("label", { className: "field", textContent: "Общая часть" }), general,
    el("label", { className: "field", textContent: "Желаемая зарплата на руки, ₽" }), desiredSalary,
    el("label", { className: "field", textContent: "Готов к тестовому заданию" }), readyForTest,
    el("p", { className: "muted", textContent: "Эти два поля LLM не получает: сервер дописывает их к письму, только если работодатель просит. Пусто — отклик с такой просьбой уйдёт на проверку." }),
    el("label", { className: "field", textContent: `Факты по навыкам (${profile.skills.length})` }), rows,
    readOnly() ? null : el("div", { className: "actions" }, add, save));

  form.onsubmit = (e) => {
    e.preventDefault();
    run(save, () => put("/api/profile", { general: general.value.trim() || null, skills: [...rows.children].map((r) => r.read()),
      desiredSalaryRub: desiredSalary.value ? Number(desiredSalary.value) : null,
      readyForTestTask: readyForTest.value === "" ? null : readyForTest.value === "true" }),
      (saved) => { render(); return `Профиль кандидата сохранён: навыков ${saved.skills.length}`; });
  };
  return form;
}

function resumeForm(r) {
  const inputs = {
    name: el("input", { value: r?.name ?? "", required: true }),
    match: el("input", { value: r?.match ?? "", required: true }),
    keywords: el("input", { value: (r?.keywords ?? []).join(", ") }),
    summary: el("textarea", { value: r?.summary ?? "" }),
    letterTemplate: el("textarea", { value: r?.letterTemplate ?? "" }),
  };
  if (readOnly()) Object.values(inputs).forEach((i) => (i.disabled = true));

  const save = el("button", { className: "primary", textContent: r ? "Сохранить" : "Добавить" });
  const form = el("form", { className: "card" },
    el("h2", { textContent: r ? r.name : "Новый профиль резюме" }),
    el("div", { className: "form" },
      field("Название", inputs.name), field("Совпадение", inputs.match), field("Ключевые слова", inputs.keywords),
      field("Описание для LLM", inputs.summary, true), field("Шаблон письма", inputs.letterTemplate, true)),
    readOnly() ? null : el("div", { className: "bar", style: "margin:12px 0 0" }, save));

  form.onsubmit = (e) => {
    e.preventDefault();
    run(save, () => put("/api/resumes", {
      name: inputs.name.value.trim(), match: inputs.match.value.trim(), keywords: lines(inputs.keywords.value),
      summary: inputs.summary.value.trim() || null, letterTemplate: inputs.letterTemplate.value.trim() || null,
    }), (saved) => { render(); return `Резюме «${saved.name}» сохранено`; });
  };
  return form;
}

async function account() {
  const [tokens, telegram] = await Promise.all([api("/api/tokens"), api("/api/telegram")]);

  // Пароль
  const current = el("input", { type: "password", autocomplete: "current-password", required: true });
  const fresh = el("input", { type: "password", autocomplete: "new-password", required: true, minLength: 10 });
  const change = el("button", { className: "primary", textContent: "Сменить пароль" });
  const passwordForm = el("form", { className: "card" }, el("h2", { textContent: "Пароль" }),
    el("div", { className: "form" }, field("Текущий", current), field("Новый", fresh)),
    el("div", { className: "bar", style: "margin:12px 0 0" }, change),
    el("p", { className: "muted", textContent: "Остальные сессии будут завершены." }));
  passwordForm.onsubmit = (e) => {
    e.preventDefault();
    run(change, () => post("/api/auth/password", { currentPassword: current.value, newPassword: fresh.value }),
      () => { current.value = fresh.value = ""; return "Пароль изменён"; });
  };

  // Персональные токены — для расширения
  const tokenName = el("input", { placeholder: "Название, например «ноутбук»", "aria-label": "Название токена", required: true });
  const issue = el("button", { className: "primary", textContent: "Выпустить" });
  const issued = el("div");
  const tokenForm = el("form", { className: "bar" }, tokenName, issue);
  tokenForm.onsubmit = async (e) => {
    e.preventDefault();
    const token = await run(issue, () => post("/api/tokens", { name: tokenName.value.trim() }), "Токен выпущен");
    if (!token) return;
    tokenName.value = "";
    tokensTable.replaceWith(tokensTable = tokenRows(await api("/api/tokens")));
    issued.replaceChildren(el("p", { textContent: "Скопируйте токен в панель расширения — он показывается один раз:" }), el("div", { className: "secret", textContent: token.value }));
  };
  let tokensTable = tokenRows(tokens);

  function tokenRows(items) {
    if (!items.length) return el("p", { className: "muted", textContent: "Токенов нет." });
    return stackTable([{ title: "Название" }, { title: "Начало" }, { title: "Выпущен" }, { title: "" }],
      items.map((t) => {
        const revoke = el("button", { className: "danger", textContent: "Отозвать" });
        revoke.onclick = () => {
          if (confirm(`Отозвать токен «${t.name}»? Расширение с ним перестанет работать.`))
            run(revoke, () => api(`/api/tokens/${t.id}`, { method: "DELETE" }), () => { render(); return `Токен «${t.name}» отозван`; });
        };
        return [t.name, el("span", { className: "mono", textContent: `${t.prefix}…` }), date(t.createdAt), t.revokedAt ? `отозван ${date(t.revokedAt)}` : revoke];
      }));
  }

  // Telegram
  const tgBox = el("div");
  function telegramView(t) {
    if (!t.configured) return el("p", { className: "muted", textContent: "Бот не настроен на сервере (TELEGRAM_BOT_TOKEN)." });
    if (t.linked) {
      const unlink = el("button", { className: "danger", textContent: "Отвязать" });
      unlink.onclick = () => run(unlink, () => api("/api/telegram", { method: "DELETE" }), () => { render(); return "Telegram отвязан"; });
      return el("div", { className: "bar" }, `Привязан${t.username ? ` к @${t.username}` : ""} ${date(t.linkedAt)}`, unlink);
    }
    const link = el("button", { className: "primary", textContent: "Получить ссылку привязки" });
    const out = el("div");
    link.onclick = async () => {
      const code = await run(link, () => post("/api/telegram/link"), "Код действует 15 минут");
      if (!code) return;
      out.replaceChildren(code.link
        ? el("p", {}, "Откройте ", el("a", { href: safeUrl(code.link), target: "_blank", rel: "noopener", textContent: "ссылку на бота" }), " и нажмите «Start».")
        : el("p", {}, "Отправьте боту: ", el("span", { className: "secret", textContent: `/start ${code.code}` })));
    };
    return el("div", {}, el("div", { className: "bar" }, "Не привязан", link), out);
  }
  tgBox.append(telegramView(telegram));

  return [
    el("h1", { textContent: "Профиль" }),
    el("p", { className: "sub", textContent: `${me.name}, роль ${me.role}` }),
    me.viaApiKey ? el("p", { className: "card", textContent: "Вход по токену: пароль, токены и Telegram меняются только после входа по паролю." }) : passwordForm,
    el("div", { className: "card" }, el("h2", { textContent: "Токены расширения" }), tokenForm, issued, tokensTable),
    el("div", { className: "card" }, el("h2", { textContent: "Telegram" }), tgBox),
    el("div", { className: "card" }, el("h2", { textContent: "Отчёт" }), el("a", { href: "/api/report?format=csv", textContent: "Скачать отчёт по откликам (CSV)" })),
  ];
}

const collectionCounts = (r) => `просмотрено ${r.seen}, новых ${r.added}, в очередь ${r.queued}, отсеяно ${r.skipped}`;

function collectionText(state) {
  const { current, last } = state;
  if (current) {
    const place = current.search ? `поиск «${current.search}», страница ${current.page}: ` : "";
    return `Идёт сбор (${current.trigger}) с ${date(current.startedAt)}. ${place}${collectionCounts(current)}`;
  }
  if (!last) return "С момента запуска сервера сбор ещё не завершался.";
  const result = last.error ? `Остановлен: ${last.error}. ` : "";
  return `Последний сбор (${last.trigger}) закончился ${date(last.finishedAt)}. ${result}${last.searches ? `Поисков ${last.searches}, ` : ""}${collectionCounts(last)}`;
}

// Сбор идёт на сервере (#74): кнопка только запускает его, а страница читает состояние, поэтому обновление страницы ничего не прерывает
function collectionCard() {
  const page = renders;
  const text = el("p", { textContent: "Загрузка состояния…" });
  const button = el("button", { className: "primary", textContent: "Собрать вакансии", disabled: true });
  let timer;

  async function refresh() {
    clearTimeout(timer);
    let state;
    try {
      state = await api("/api/collect/status");
    } catch (e) {
      if (e instanceof Unauthorized) return showLogin();
      if (page === renders) timer = setTimeout(refresh, 15000);
      text.textContent = `Состояние сбора недоступно: ${e.message}`;
      return;
    }
    // Ушли с раздела или он перерисован: опрашивает уже новая карточка
    if (page !== renders) return;
    text.textContent = collectionText(state);
    button.disabled = Boolean(state.current);
    // Пока сбор не идёт, реже: только заметить проход по расписанию
    timer = setTimeout(refresh, state.current ? 2000 : 15000);
  }

  button.onclick = async () => {
    await run(button, () => post("/api/collect/start"), "Сбор запущен");
    refresh();
  };
  refresh();

  return el("div", { className: "card" }, el("h2", { textContent: "Сбор вакансий" }),
    el("p", { className: "muted", textContent: "Идёт сам раз в час. Запущенный сбор продолжается на сервере, страницу можно обновлять и закрывать." }),
    text, el("div", { className: "bar", style: "margin:0" }, button));
}

async function admin() {
  if (me.role !== "Admin") return [el("p", { className: "empty", textContent: "Раздел только для администратора." })];
  const users = await api("/api/users");

  const rows = users.map((u) => {
    const self = u.id === me.id;
    const role = el("select", { disabled: self, "aria-label": "Роль" }, el("option", { value: "User", textContent: "User" }), el("option", { value: "Admin", textContent: "Admin" }));
    role.value = u.role;
    role.onchange = () => run(role, () => put(`/api/users/${u.id}/role`, { role: role.value }), `${u.email}: роль ${role.value}`);
    const block = el("button", { textContent: u.blocked ? "Разблокировать" : "Заблокировать", className: u.blocked ? "" : "danger", disabled: self });
    block.onclick = () => run(block, () => put(`/api/users/${u.id}/blocked`, { blocked: !u.blocked }), () => { render(); return `${u.email}: ${u.blocked ? "разблокирован" : "заблокирован"}`; });
    const reset = el("button", { textContent: "Задать пароль" });
    reset.onclick = () => {
      const password = prompt(`Новый пароль для ${u.email} (не короче 10 символов)`);
      if (password) run(reset, () => put(`/api/users/${u.id}/password`, { password }), `${u.email}: пароль задан`);
    };
    return [u.email, role, date(u.createdAt), el("div", { className: "actions", style: "margin:0" }, block, reset)];
  });

  const email = el("input", { type: "email", placeholder: "Email", "aria-label": "Email", required: true });
  const password = el("input", { type: "password", placeholder: "Пароль", "aria-label": "Пароль", autocomplete: "new-password", required: true, minLength: 10 });
  const newRole = el("select", { "aria-label": "Роль" }, el("option", { value: "User", textContent: "User" }), el("option", { value: "Admin", textContent: "Admin" }));
  const create = el("button", { className: "primary", textContent: "Создать" });
  const createForm = el("form", { className: "bar" }, email, password, newRole, create);
  createForm.onsubmit = (e) => {
    e.preventDefault();
    run(create, () => post("/api/users", { email: email.value.trim(), password: password.value, role: newRole.value }),
      () => { render(); loadUsers(); return `Пользователь ${email.value.trim()} создан`; });
  };

  // Ручной запуск фоновых проходов: обычно они идут сами (сбор — раз в час, LLM — раз в минуту при наличии работы)
  const jobs = [
    ["Проверить LLM", "/api/llm/check", undefined, (r) => `LLM ${r.model}: ответ за ${r.latencyMs} мс`],
    ["Оценить ожидающие", "/api/llm/evaluate", { limit: 20 }, (r) => `Оценено ${r.evaluated}: подходят ${r.fit}, сомнительных ${r.doubtful}, отклонено ${r.rejected}; осталось ${r.remaining}`],
    ["Ответить на анкеты", "/api/llm/answers", { limit: 20 }, (r) => `Анкеты: отвечено ${r.answered}, не вышло ${r.failed}; осталось ${r.remaining}`],
    ["Написать письма", "/api/llm/letters", { limit: 20 }, (r) => `Письма: написано ${r.written}, не вышло ${r.failed}; осталось ${r.remaining}`],
  ].map(([text, path, body, done]) => {
    const button = el("button", { textContent: text });
    button.onclick = () => { setStatus(`${text}…`); run(button, () => post(path, body), done); };
    return button;
  });

  return [
    el("h1", { textContent: "Администрирование" }),
    el("div", { className: "card" }, el("h2", { textContent: "Пользователи" }),
      stackTable([{ title: "Email" }, { title: "Роль" }, { title: "Создан" }, { title: "" }], rows),
      el("h2", { textContent: "Новый пользователь", style: "margin-top:16px" }), createForm),
    collectionCard(),
    el("div", { className: "card" }, el("h2", { textContent: "Запустить сейчас" }),
      el("p", { className: "muted", textContent: "Обычно не нужно: LLM работает раз в минуту, если есть работа. Тратит лимит LLM." }),
      el("div", { className: "bar", style: "margin:0" }, jobs)),
  ];
}

// ── Каркас ───────────────────────────────────────────────────────────

const routes = { dashboard, overview, vacancies, review, dialogs, searches, resumes, account, admin };

// Разделы (#120): tab — место в нижней панели телефона (остальные — в «Ещё»), icon — значок там же
const SECTIONS = [
  { route: "dashboard", title: "Дашборд", icon: "◫", tab: true },
  { route: "review", title: "Проверка", icon: "✎", tab: true, count: "review" },
  { route: "dialogs", title: "Ответы", icon: "✉", tab: true, count: "dialogs" },
  { route: "vacancies", title: "Вакансии", icon: "☰", tab: true },
  { route: "overview", title: "Обзор" },
  { route: "searches", title: "Поиски" },
  { route: "resumes", title: "Резюме" },
  { route: "account", title: "Профиль" },
  { route: "admin", title: "Администрирование", admin: true },
];
const counts = { review: 0, dialogs: 0 };
const tabbar = document.getElementById("tabbar");
const more = document.getElementById("more");

function countBadge(s) {
  const n = s.count ? counts[s.count] : 0;
  return n ? el("span", { className: "count", textContent: n > 99 ? "99+" : n }) : null;
}

function buildNav(route) {
  const visible = SECTIONS.filter((s) => !s.admin || me?.role === "Admin");
  nav.replaceChildren(...visible.map((s) => el("a", { href: `#${s.route}`, className: s.route === route ? "active" : "" }, s.title, countBadge(s))));
  const rest = visible.filter((s) => !s.tab);
  const moreOn = rest.some((s) => s.route === route);
  const moreButton = el("button", { type: "button", className: moreOn ? "active" : "", "aria-label": "Ещё разделы" }, el("b", { textContent: "⋯" }), "Ещё");
  moreButton.onclick = (e) => { e.stopPropagation(); more.hidden = !more.hidden; };
  tabbar.replaceChildren(...visible.filter((s) => s.tab).map((s) =>
    el("a", { href: `#${s.route}`, className: s.route === route ? "active" : "" }, el("b", { textContent: s.icon }), s.title, countBadge(s))), moreButton);
  more.replaceChildren(...rest.map((s) => el("a", { href: `#${s.route}`, className: s.route === route ? "active" : "" }, s.title)));
  more.hidden = true;
}

// Счётчики в навигации: на проверке и необработанные ответы (кроме отказов) — как в «Ответах работодателей»
async function loadCounts() {
  const [stats, open] = await Promise.all([
    api(scoped("/api/stats")).catch(() => ({})),
    api(scoped("/api/dialogs?take=200&handled=false")).catch(() => []),
  ]);
  counts.review = stats.Review ?? 0;
  counts.dialogs = open.filter((m) => m.kind !== "Rejection").length;
}

document.addEventListener("click", (e) => { if (!more.hidden && !more.contains(e.target)) more.hidden = true; });

// Тема: системная → тёмная → светлая; выбор — в localStorage (может быть недоступен — тогда только на эту вкладку)
const THEMES = [["system", "◐", "Тема: как в системе"], ["dark", "●", "Тема: тёмная"], ["light", "○", "Тема: светлая"]];
const themeButton = document.getElementById("theme");
function applyTheme(name) {
  if (name === "system") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = name;
  const [, icon, title] = THEMES.find(([n]) => n === name);
  themeButton.textContent = icon;
  themeButton.title = title;
  themeButton.setAttribute("aria-label", title);
}
themeButton.onclick = () => {
  const now = document.documentElement.dataset.theme ?? "system";
  const next = THEMES[(THEMES.findIndex(([n]) => n === now) + 1) % THEMES.length][0];
  try { next === "system" ? localStorage.removeItem("theme") : localStorage.setItem("theme", next); } catch {}
  applyTheme(next);
};
applyTheme(document.documentElement.dataset.theme ?? "system");

// Номер последней отрисовки: раздел, загрузившийся позже следующего перехода, не перерисовывает страницу
let renders = 0;

async function render() {
  if (!me) return;
  const current = ++renders;
  const [name, arg] = location.hash.slice(1).split("/");
  const route = routes[name] ? name : "dashboard";
  buildNav(route);
  try {
    const content = await routes[route](arg);
    if (current !== renders) return;
    view.replaceChildren(...[content].flat(Infinity).filter(Boolean));
  } catch (e) {
    if (current !== renders) return;
    view.replaceChildren();
    if (e instanceof Unauthorized) showLogin();
    setStatus(e.message, true);
  }
}

async function loadUsers() {
  if (me.role !== "Admin") return;
  const users = await api("/api/users").catch(() => []);
  viewAs.replaceChildren(el("option", { value: "", textContent: "мои" }),
    ...users.filter((u) => u.id !== me.id).map((u) => el("option", { value: u.id, textContent: u.email })));
  viewAs.value = viewedUser ?? "";
  document.getElementById("viewing").hidden = users.length < 2;
}

function showLogin() {
  me = null;
  view.replaceChildren();
  nav.hidden = tabbar.hidden = more.hidden = true;
  document.getElementById("logout").hidden = true;
  document.getElementById("viewing").hidden = true;
  document.getElementById("who").textContent = "";
  loginForm.hidden = false;
  document.getElementById("email").focus();
}

async function showSession(user) {
  me = user;
  loginForm.hidden = true;
  nav.hidden = tabbar.hidden = false;
  document.getElementById("logout").hidden = false;
  document.getElementById("who").textContent = user.name;
  await Promise.all([loadUsers(), loadCounts()]);
  await render();
}

loginForm.onsubmit = async (e) => {
  e.preventDefault();
  const password = document.getElementById("password");
  try {
    await post("/api/auth/login", { email: document.getElementById("email").value.trim(), password: password.value });
    password.value = "";
    setStatus("");
    // Вход отвечает учётной записью без признака входа по токену — берём то же, что при открытии страницы
    await showSession(await api("/api/auth/me"));
  } catch (err) {
    setStatus(err.message, true);
  }
};

document.getElementById("logout").onclick = async () => {
  await post("/api/auth/logout").catch(() => null);
  setStatus("");
  showLogin();
};

viewAs.onchange = () => {
  viewedUser = viewAs.value || null;
  setStatus(viewedUser ? `Данные ${viewAs.selectedOptions[0].textContent} — только просмотр` : "");
  loadCounts().then(render);
};

window.addEventListener("hashchange", () => { setStatus(viewedUser ? status.textContent : ""); render(); });

// Сессия могла остаться с прошлого раза
api("/api/auth/me").then(showSession).catch(() => showLogin());
