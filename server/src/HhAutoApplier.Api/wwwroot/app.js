"use strict";
// Единый веб-интерфейс (#46): разделы по хешу адреса поверх того же API, что у расширения. Вход — пароль, дальше cookie.
// Администратор может смотреть данные другого пользователя (?userId=) — только чтение: правки API делает от своего имени.
// Разметка — компонентами UI kit (ui/ui.js, #129); данные с hh.ru и от LLM — только текстом

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

// Источник вакансии (задел под другие агрегаторы, #32): пока все вакансии — с hh.ru
const SOURCES = { hh: { name: "hh.ru", color: "var(--src-hh)" } };
const sourcePill = (v) => {
  const s = SOURCES[v.source ?? "hh"] ?? { name: v.source, color: "var(--src-other)" };
  return Pill(s.name, { dot: s.color, title: "Источник вакансии" });
};

const date = (value) => (value ? new Date(value).toLocaleString("ru-RU", { dateStyle: "short", timeStyle: "short" }) : "");
const readOnly = () => viewedUser !== null;

const STATUSES = {
  New: "новая", Pending: "ждёт оценки", Review: "на проверке", Questionnaire: "анкета", Queued: "в очереди",
  Applied: "отклик отправлен", Invited: "приглашение", Rejected: "отказ", Skipped: "пропущена", Failed: "ошибка",
};

// Действие: итог — в строке статуса, нужен вход — форма входа. Кнопку на время запроса держит занятой Button или busy()
async function act(action, done) {
  try {
    const result = await action();
    if (done) setStatus(typeof done === "function" ? done(result) : done);
    return result;
  } catch (e) {
    if (e instanceof Unauthorized) showLogin();
    setStatus(e.message, true);
  }
}

// ── Разделы ──────────────────────────────────────────────────────────

// Дашборд (#121), демо: считается в браузере по тем же данным, что «Вакансии» (история статусов каждого отклика).
// В задаче — эндпоинт GET /api/dashboard: тогда страница перестанет выкачивать все отклики
const DAY = 86400000;
const PERIODS = [7, 30, 90];
const AGGREGATORS = [
  { value: "all", label: "Все" },
  { value: "hh", label: "hh.ru", color: "var(--src-hh)" },
  { value: "hirify", label: "hirify", color: "var(--src-hirify)", disabled: true, title: "После подключения (#32)" },
  { value: "getmatch", label: "getmatch", color: "var(--src-getmatch)", disabled: true, title: "После подключения (#32)" },
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

    // Дельта к прошлому периоду той же длины
    const delta = (a, b) => {
      if (!b) return { note: a ? "новое за период" : "—" };
      const d = Math.round(((a - b) / b) * 100);
      return { trend: d > 0 ? "up" : d < 0 ? "down" : null, note: `${d > 0 ? "+" : ""}${d}% к прошлым ${dashState.period} дн` };
    };

    // Воронка — по вакансиям, собранным за период: % — от предыдущего шага, отказ — от отправленных
    const step = (pred) => cohort.filter(pred).length;
    const f = [
      ["Собрано", cohort.length],
      ["Оценено LLM", step((v) => v.llmScore != null)],
      ["В очередь", step((v) => reached(v, "Queued") != null || reached(v, "Applied") != null)],
      ["Отправлено", step((v) => reached(v, "Applied") != null)],
      ["Приглашение", step((v) => reached(v, "Invited") != null), "var(--ok)"],
    ];
    const sent = f[3][1];
    const rejectedCohort = step((v) => reached(v, "Rejected") != null);

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
    const dayLabel = (t) => new Date(t).toLocaleDateString("ru-RU", { day: "numeric", month: "short" });
    // Столбец — больше из «отправлено» и «ответили»; внутри — приглашения и отказы
    const bars = Bars(series.map((s) => ({
      total: Math.max(s.sent, s.answered),
      title: `${dayLabel(s.at)}${bucket > 1 ? " (неделя)" : ""}: отправлено ${s.sent}, ответов ${s.answered}, приглашений ${s.invited}`,
      parts: [{ value: s.answered ? s.invited : 0, tone: "ok" }, { value: s.answered ? s.answered - s.invited : 0, tone: "accent" }],
    })), {
      from: dayLabel(first), to: "сегодня",
      legend: [{ label: "отправлено", color: "var(--bar)" }, { label: "ответили отказом", color: "var(--accent)" }, { label: "приглашение", color: "var(--ok)" }],
    });

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
    const sourceName = AGGREGATORS.find((a) => a.value === dashState.source).label;

    root.replaceChildren(
      PageHead("Дашборд", [
        lastChat ? `последнее сообщение из чатов ${date(lastChat)}` : "чаты ещё не синхронизировались",
        lastCollect,
      ].filter(Boolean).join(" · "),
      Segmented(PERIODS.map((p) => ({ value: p, label: `${p} дн` })), dashState.period, (p) => { dashState.period = p; draw(); }, { label: "Период" })),
      Chips(AGGREGATORS, dashState.source, (s) => { dashState.source = s; draw(); }, { label: "Агрегатор" }),
      Note("Демо #121: числа считаются в браузере по истории откликов. Ответы работодателей — на момент последней синхронизации чатов в расширении."),
      Stats([
        Stat({ value: cohort.length, label: "собрано вакансий", ...delta(cohort.length, prevCohort.length) }),
        Stat({ value: applied.length, label: "откликов отправлено", ...delta(applied.length, prevApplied.length) }),
        Stat({ value: pct(invited.length, applied.length), unit: "%", label: "в приглашение", note: `${invited.length} приглашений за период` }),
        Stat({ value: rejected.length, label: "отказов", note: "собеседования — после #122" }),
      ]),
      el("div", { className: "cols" },
        Card({}, Caption(`Воронка за ${dashState.period} дней · ${sourceName}`),
          Funnel([
            ...f.map(([label, value, color], i) => ({ label, value, base: i ? f[i - 1][1] : null, color })),
            { label: "Отказ", value: rejectedCohort, base: sent, color: "var(--bad)" },
          ], { max: cohort.length, note: "% — от предыдущего шага; отказ — от отправленных" })),
        Card({}, Caption(bucket > 1 ? "Отклики по неделям" : "Отклики по дням"), bars)),
      el("div", { className: "cols wide-left" },
        Card({}, Caption(`По поискам · ${sourceName}`),
          searchRows.length ? StackTable([{ title: "Поиск" }, { title: "Собрано", num: true }, { title: "Отправлено", num: true }, { title: "Ответили", num: true }, { title: "Приглашения", num: true }, { title: "Конверсия", num: true }], searchRows)
            : EmptyState("За период ничего не собрано."),
          el("p", { className: "muted small", style: "margin:8px 0 0", textContent: `Поисков включено: ${searchList.filter((s) => s.enabled).length} из ${searchList.length}` })),
        Card({}, Caption("Почему отсеяно"),
          topReasons.length ? RankList(topReasons) : EmptyState("Отсеянных за период нет."))));
  }

  draw();
  return root;
}

async function overview() {
  const [stats, telegram] = await Promise.all([api(scoped("/api/stats")), readOnly() ? null : api("/api/telegram").catch(() => null)]);
  const order = ["Queued", "Review", "Questionnaire", "Pending", "Applied", "Invited", "Rejected", "Skipped", "Failed", "New"];
  const total = Object.values(stats).reduce((a, b) => a + b, 0);
  const tiles = order.filter((s) => stats[s]).map((s) => Stat({ value: stats[s], label: STATUSES[s], href: `#vacancies/${s}` }));

  return [
    PageHead("Обзор", `Всего откликов в работе: ${total}. Нажмите на счётчик — откроется список.`),
    tiles.length ? Stats(tiles, { tiles: true }) : EmptyState("Откликов пока нет: добавьте поиск — сервер соберёт вакансии."),
    Card({ title: "Что дальше" },
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

// Список вариантов для <select>: [[value, text]]
const options = (pairs) => pairs.map(([value, textContent]) => el("option", { value, textContent }));

async function vacancies(statusFilter) {
  const take = 50;
  const searches = await api(scoped("/api/searches"));
  const state = { status: statusFilter ?? "", search: "", hidden: false, skip: 0 };

  const statusSelect = el("select", { "aria-label": "Статус" }, options([["", "Все статусы"], ...Object.entries(STATUSES)]));
  statusSelect.value = state.status;
  const searchSelect = el("select", { "aria-label": "Поиск" }, options([["", "Все поиски"], ...searches.map((s) => [s.name, s.name])]));
  const hidden = el("input", { type: "checkbox" });
  const prev = Button("← Назад", { onClick: () => { state.skip = Math.max(0, state.skip - take); load(); } });
  const next = Button("Дальше →", { onClick: () => { state.skip += take; load(); } });
  const page = el("span", { className: "muted" });
  const list = el("div");

  async function load() {
    const query = new URLSearchParams({ skip: state.skip, take });
    if (state.status) query.set("status", state.status);
    if (state.search) query.set("search", state.search);
    if (state.hidden) query.set("includeHidden", "true");
    try {
      const items = await api(scoped(`/api/vacancies?${query}`));
      list.replaceChildren(...(items.length ? items.map(vacancyCard) : [EmptyState("Ничего нет.")]));
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
  await load();

  return [el("h1", { textContent: "Вакансии" }), Toolbar([statusSelect, searchSelect, el("label", {}, hidden, " Показать скрытые"), prev, next, page]), list];
}

function salary(v) {
  if (v.salaryFrom == null && v.salaryTo == null) return null;
  const range = [v.salaryFrom != null ? `от ${v.salaryFrom.toLocaleString("ru-RU")}` : "", v.salaryTo != null ? `до ${v.salaryTo.toLocaleString("ru-RU")}` : ""];
  return `${range.filter(Boolean).join(" ")} ${v.salaryCurrency ?? ""}`.trim();
}

// Решения по вакансии вне ручной проверки: пропущенную или упавшую — снова в очередь, ожидающую — пропустить (забыть)
const REQUEUE = ["Skipped", "Failed"];
const SKIPPABLE = ["Queued", "Pending", "Questionnaire", "New"];
const vacancyPath = (v, action) => `/api/vacancies/${encodeURIComponent(v.id)}/${action}`;

function vacancyActions(v, pill) {
  if (readOnly()) return null;
  if (v.status === "Review") return Actions([el("a", { href: "#review", textContent: "Решить в «Проверке» →" })]);

  const result = Result();
  // Итог на месте кнопок; карточка приглушается («решено»), статус в шапке — новый
  const finish = (text, status) => {
    Card.decide(result);
    result.textContent = text;
    if (status) pill.textContent = STATUSES[status];
    return `«${v.title}»: ${status ? STATUSES[status] : text}`;
  };

  if (v.hiddenAt) {
    const unhide = Button("Вернуть", { onClick: () => act(() => post(vacancyPath(v, "unhide")), () => { unhide.remove(); return finish("возвращена в список"); }) });
    return Actions([unhide, result]);
  }

  if (REQUEUE.includes(v.status)) {
    // Оценить заново и скрыть — только отсеянные: у Failed отклик мог уйти на hh.ru
    const clear = () => [requeue, reevaluate, hide].forEach((b) => b?.remove());
    const requeue = Button("В очередь", { kind: "primary",
      onClick: () => act(() => post(vacancyPath(v, "status"), { status: "Queued" }), () => { clear(); return finish("в очереди", "Queued"); }) });
    const reevaluate = v.dismissed ? Button("Оценить заново", {
      onClick: () => act(() => post(vacancyPath(v, "reevaluate")), (r) => {
        clear();
        return finish(r.status === "Skipped" ? `снова отсеяна: ${r.statusReason ?? ""}` : STATUSES[r.status], r.status);
      }) }) : null;
    const hide = v.dismissed ? Button("Скрыть", { onClick: () => act(() => post(vacancyPath(v, "hide")), () => { clear(); return finish("скрыта"); }) }) : null;
    return Actions([requeue, reevaluate, hide, result]);
  }

  if (SKIPPABLE.includes(v.status)) {
    const reason = el("input", { placeholder: "Причина (необязательно)", "aria-label": "Причина пропуска" });
    const skip = Button("Пропустить", { kind: "danger",
      onClick: () => act(() => post(vacancyPath(v, "status"),
        { status: "Skipped", reason: reason.value.trim() ? `пропущено вручную: ${reason.value.trim()}` : "пропущено вручную" }),
      () => { skip.remove(); reason.remove(); return finish("пропущена", "Skipped"); }) });
    return Actions([reason, skip, result]);
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
  return v.letterRequests?.length ? Disclosure(`Просьбы к письму (${v.letterRequests.length})`, v.letterRequests.map(letterRequestLine), { open: v.letterRequestsState === "Found" }) : null;
}

function vacancyCard(v) {
  const pill = Pill(STATUSES[v.status] ?? v.status);
  return Card({ as: "article",
    head: el("div", {}, el("a", { href: safeUrl(v.url), target: "_blank", rel: "noopener", textContent: v.title, title: "Открыть вакансию" }), " ", pill, " ", sourcePill(v),
      v.hiddenAt ? [" ", Pill("скрыта")] : null, v.vacancyArchived ? [" ", Pill("в архиве")] : null),
    meta: [v.company, v.area, salary(v), v.workFormat, v.workExperience && `опыт: ${v.workExperience}`, `балл ${v.llmScore ?? v.score}`, v.resumeProfile && `резюме ${v.resumeProfile}`, v.searchName && `поиск ${v.searchName}`, v.appliedAt && `отклик ${date(v.appliedAt)}`] },
  // Вакансия общая: даты — когда её нашли впервые и когда она встретилась в сборе или импорте последний раз (#70)
  Meta([v.collectedAt && `найдена ${date(v.collectedAt)}`, v.lastSeenAt && `встречалась ${date(v.lastSeenAt)}`]),
  v.statusReason || v.llmReason ? el("div", { className: "reason", textContent: v.statusReason ?? v.llmReason }) : null,
  letterRequestsBlock(v),
  v.coverLetter ? Disclosure("Письмо", Pre(v.coverLetter)) : null,
  v.history?.length ? Disclosure(`История (${v.history.length})`,
    StackTable([{ title: "Когда" }, { title: "Статус" }, { title: "Причина" }], v.history.map((h) => [date(h.at), STATUSES[h.to] ?? h.to, h.reason ?? ""]))) : null,
  vacancyActions(v, pill));
}

// Ручная проверка (#25, #58): вакансии, в которых LLM сомневается, и анкеты с текстовыми ответами LLM.
// «Одобрить» — в очередь с письмом и ответами из полей, «Отклонить» — пропуск с причиной
async function review() {
  const items = (await api(scoped("/api/vacancies?status=Review&take=200"))).sort((a, b) => b.score - a.score);
  return [
    PageHead("Проверка", `Вакансии, в которых LLM сомневается, и анкеты с ответами LLM: ${items.length}. Письмо и текстовые ответы можно поправить перед одобрением.`),
    el("div", {}, items.length ? items.map(reviewCard) : EmptyState("На проверке ничего нет.")),
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
    const select = el("select", { id, disabled: readOnly(), "aria-label": q.question }, options([["", "— не выбрано —"], ...q.options.map((o) => [o, o])]));
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
  const letter = el("textarea", { value: v.coverLetter ?? "", disabled: readOnly(),
    placeholder: awaitsLetter ? "LLM ещё не написала письмо. Без письма одобрить нельзя: шаблон расширения не ответит на просьбы работодателя." : "LLM ещё не написала письмо. Напишите своё или одобрите без письма — расширение возьмёт шаблон." });
  letter.id = `letter-${v.id}`;
  const reason = el("input", { placeholder: "Причина отказа (необязательно)", "aria-label": "Причина отказа" });
  const result = Result();
  // Ответы LLM на анкету (#58) — в полях: уходят работодателю от имени кандидата. Правится каждый вопрос (#115):
  // текст — полем, один вариант — списком, несколько — галочками. Нераспознанный вопрос (Unknown) — только показ
  const questions = v.questions ?? [];
  const answered = questions.some((q) => q.answer.length);
  const answerFields = questions.map((q, i) => (answered ? answerField(v, q, i) : null));
  const fields = () => [letter, ...answerFields.filter(Boolean).flatMap((f) => f.inputs)];

  // Кодовое слово вставляется только по подтверждению, значения из профиля — по кнопке после его заполнения. Ссылки и контакты не выполняются
  const resolveRequests = (confirmCodeWords) => act(() => post(vacancyPath(v, "letter-requests/resolve"), { confirmCodeWords }),
    (updated) => { node.replaceWith(reviewCard(updated)); return `«${v.title}»: письмо обновлено`; });
  const requestRows = (v.letterRequests ?? []).map((r, i) => {
    if (readOnly() || r.closed || r.type !== "CodeWord" || !r.value) return letterRequestLine(r);
    return el("div", {}, letterRequestLine(r), Actions([Button(`Вставить слово «${r.value}»`, { onClick: () => resolveRequests([i]) })]));
  });
  const needsProfile = (v.letterRequests ?? []).some((r) => !r.closed && (r.type === "Salary" || r.type === "TestTask"));
  const refresh = needsProfile && !readOnly() && v.letterRequestsState === "Found" ? Button("Подставить из профиля", { onClick: () => resolveRequests([]) }) : null;

  // Карточка после решения остаётся на месте с итогом: если убрать её, следующая подъедет под курсор и лишний клик решит за неё
  async function decide(body, done) {
    approve.disabled = decline.disabled = reason.disabled = true;
    fields().forEach((f) => (f.disabled = true));
    try {
      await post(vacancyPath(v, "review"), body);
      Card.decide(node);
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
  const approve = Button("Одобрить", { kind: "ok", onClick: () => {
    const text = letter.value.trim();
    const edited = answerFields.some((f, i) => f && !sameAnswer(f.value(), questions[i]));
    const answers = edited ? questions.map((q, i) => (answerFields[i] ? answerFields[i].value() : q.answer)) : null;
    decide({ approve: true, coverLetter: text && text !== (v.coverLetter ?? "").trim() ? text : null, answers }, "одобрено, в очереди");
  } });
  const decline = Button("Отклонить", { kind: "danger", onClick: () => { decide({ approve: false, reason: reason.value.trim() || null }, "отклонено"); } });

  const node = Card({ as: "article", title: v.title, href: v.url,
    meta: [v.company, v.area, salary(v) ?? "зарплата не указана", v.workFormat, v.workExperience && `опыт: ${v.workExperience}`, `балл ${v.score}`, v.resumeProfile && `резюме ${v.resumeProfile}`, v.hasTest && "есть тест"],
    actions: readOnly() ? null : [approve, reason, decline, result] },
  Callout(v.statusReason ?? v.llmReason ?? "LLM сомневается"),
  v.stack?.length ? Meta(`Стек: ${v.stack.join(", ")}`) : null,
  Disclosure("Описание вакансии", Pre(v.description || "описания нет", { scroll: true })),
  questions.length ? Disclosure(`Анкета (${questions.length})`,
    questions.map((q, i) => answerFields[i]
      ? el("div", { className: "reason" }, answerFields[i].label, answerFields[i].node)
      : el("div", { className: "reason" }, el("b", { textContent: q.question }), el("br"), q.answer.length ? q.answer.join("; ") : "ответа нет")), { open: answered }) : null,
  v.letterRequestsState === "Failed" ? letterRequestsBlock(v) : null,
  requestRows.length ? Disclosure(`Просьбы к письму (${requestRows.length})`, [requestRows, refresh ? Actions([refresh]) : null], { open: true }) : null,
  Field(v.coverLetterModel ? `Письмо (${v.coverLetterModel === "manual" ? "ваше" : v.coverLetterModel})` : "Письмо", letter, { stacked: true }));
  return node;
}

// Ответы работодателей (#26): разбор LLM и черновик; отвечает человек — в чате hh.ru
const KINDS = { Invitation: "Приглашение", Question: "Вопрос", Rejection: "Отказ", Other: "Прочее" };
const KIND_TONES = { Invitation: "ok", Question: "accent" };
const AUTO_REPLY = { Pending: "автоответ ждёт отправки", Sent: "автоответ отправлен", Unavailable: "автоответ невозможен" };

async function dialogs(filter = "open") {
  const open = filter === "open";
  const items = (await api(scoped(`/api/dialogs?take=200${open ? "&handled=false" : ""}`))).filter((m) => !open || m.kind !== "Rejection");
  const select = el("select", { "aria-label": "Какие сообщения показать" }, options([["open", "Необработанные, кроме отказов"], ["all", "Все"]]));
  select.value = open ? "open" : "all";
  select.onchange = () => { location.hash = `#dialogs/${select.value}`; };

  return [
    PageHead("Ответы работодателей", "Сообщения из чатов hh.ru присылает расширение («Автоответ» на странице чатов). LLM разбирает приглашения и вопросы и предлагает ответ — отправляете вы. Отказы распознаются без LLM."),
    Toolbar([select, el("span", { className: "muted", textContent: `Сообщений: ${items.length}` })]),
    items.length ? items.map(dialogCard)
      : EmptyState(open ? "Новых ответов работодателей нет." : "Сообщений пока нет: нажмите «Автоответ» в панели расширения на странице чатов hh.ru."),
  ];
}

function dialogCard(m) {
  const chatUrl = `https://hh.ru/chat/${encodeURIComponent(m.chatId)}`;
  const rejection = m.kind === "Rejection";
  const reply = el("textarea", { value: m.suggestedReply ?? "",
    placeholder: m.kind == null ? "LLM ещё не разобрала сообщение — обновите страницу через минуту." : "Черновика нет." });
  reply.id = `reply-${m.chatId}`;
  const result = Result();

  // Буфер обмена доступен не везде (http без localhost): тогда текст просто выделяется для Ctrl+C
  const copy = Button("Копировать и открыть чат", { kind: "primary", onClick: async () => {
    try {
      await navigator.clipboard.writeText(reply.value);
      result.textContent = "скопировано";
    } catch {
      reply.select();
      result.textContent = "выделено — скопируйте Ctrl+C";
    }
    window.open(chatUrl, "_blank", "noopener");
  } });
  const done = Button("Обработано", { onClick: () => act(() => post(`/api/dialogs/${encodeURIComponent(m.chatId)}/handled`), () => {
    Card.decide(done);
    done.remove();
    result.textContent = "обработано";
    return `«${m.title}»: обработано`;
  }) });

  return Card({ as: "article", decided: !!m.handledAt, title: m.title || "Чат без вакансии", href: chatUrl,
    actions: [rejection ? null : copy, m.handledAt || readOnly() ? null : done, result] },
  Meta([m.company, date(m.at), m.hhState && `hh: ${m.hhState}`, AUTO_REPLY[m.autoReply], m.autoReplyError, m.handledAt && "обработано"],
    { lead: Badge(KINDS[m.kind] ?? "ждёт разбора", { tone: KIND_TONES[m.kind] }) }),
  m.summary ? Callout(m.summary) : null,
  Quote(m.text),
  rejection ? null : Field(m.llmModel ? `Черновик ответа (${m.llmModel}) — поправьте [заглушки] перед отправкой` : "Черновик ответа", reply, { stacked: true }));
}

const lines = (text) => text.split(/[\n,]/).map((s) => s.trim()).filter(Boolean);

async function searches() {
  const items = await api(scoped("/api/searches"));
  const list = items.map((s) => searchForm(s));
  if (!readOnly()) list.push(searchForm(null));
  return [
    PageHead("Поиски", "Сервер собирает вакансии по включённым поискам раз в час и отбирает по правилам; остальное оценивает LLM."),
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

  const save = Button(s ? "Сохранить" : "Добавить", { kind: "primary", type: "submit" });
  const remove = s ? Button("Удалить", { kind: "danger", onClick: () => {
    if (confirm(`Удалить поиск «${s.name}»?`))
      return act(() => api(`/api/searches/${s.id}`, { method: "DELETE" }), () => { render(); return `Поиск «${s.name}» удалён`; });
  } }) : null;

  return Form({ submit: save, onSubmit: () => act(() => put("/api/searches", {
    name: inputs.name.value.trim(), query: inputs.query.value.trim(), area: inputs.area.value.trim() || null,
    extraParams: inputs.extraParams.value.trim() || null, enabled: inputs.enabled.checked,
    rules: {
      includeKeywords: lines(inputs.includeKeywords.value), excludeKeywords: lines(inputs.excludeKeywords.value), levelKeywords: lines(inputs.levelKeywords.value),
      allowedFormats: lines(inputs.allowedFormats.value), minSalaryRub: inputs.minSalaryRub.value ? Number(inputs.minSalaryRub.value) : null,
      minScore: Number(inputs.minScore.value || 0), requireSalary: inputs.requireSalary.checked,
      skipWithTest: inputs.skipWithTest.checked, skipReview: inputs.skipReview.checked,
    },
  }), (saved) => { render(); return `Поиск «${saved.name}» сохранён`; }) },
  el("h2", { textContent: s ? s.name : "Новый поиск" }),
  FormGrid(
    Field("Название", inputs.name), Field("Запрос", inputs.query), Field("Регион (area)", inputs.area), Field("Доп. параметры", inputs.extraParams),
    Field("Включён", inputs.enabled), Field("Нужные слова", inputs.includeKeywords), Field("Исключить слова", inputs.excludeKeywords), Field("Слова уровня (в названии)", inputs.levelKeywords),
    Field("Форматы работы", inputs.allowedFormats), Field("Зарплата от, ₽", inputs.minSalaryRub), Field("Мин. балл", inputs.minScore),
    Field("Только с зарплатой", inputs.requireSalary), Field("Пропускать с тестом", inputs.skipWithTest), Field("Без ручной проверки", inputs.skipReview)),
  readOnly() ? null : Toolbar([save, remove], { after: true }));
}

async function resumes() {
  const [items, profile] = await Promise.all([api(scoped("/api/resumes")), api(scoped("/api/profile"))]);
  const list = items.map((r) => resumeForm(r));
  if (!readOnly()) list.push(resumeForm(null));
  return [
    PageHead("Резюме", "Профили резюме: «совпадение» — часть названия резюме на hh.ru, по ней расширение выбирает резюме. Описание и шаблон письма читает LLM."),
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
  const readyForTest = el("select", { disabled: readOnly(), "aria-label": "Готовность к тестовому заданию" }, options([["", "не указано"], ["true", "да"], ["false", "нет"]]));
  readyForTest.value = profile.readyForTestTask == null ? "" : String(profile.readyForTestTask);
  const rows = el("div");

  function skillRow(s = { skill: "", facts: "" }) {
    const skill = el("input", { value: s.skill, placeholder: "Навык, синонимы через «/»: EF Core / Entity Framework", "aria-label": "Навык", disabled: readOnly() });
    const facts = el("textarea", { value: s.facts, placeholder: "Где применял, сколько, что делал, цифры", "aria-label": "Факты", disabled: readOnly() });
    facts.style.minHeight = "70px";
    skill.style.width = "100%";
    skill.style.marginBottom = "6px";
    const row = Card({ tight: true }, skill, facts, readOnly() ? null : Actions([Button("Убрать", { kind: "danger", onClick: () => row.remove() })]));
    row.read = () => ({ skill: skill.value.trim(), facts: facts.value.trim() });
    return row;
  }

  rows.append(...profile.skills.map(skillRow));
  if (!readOnly() && profile.skills.length === 0) rows.append(skillRow());

  const add = Button("+ Навык", { onClick: () => rows.append(skillRow()) });
  const save = Button("Сохранить профиль", { kind: "primary", type: "submit" });
  return Form({ submit: save, onSubmit: () => act(() => put("/api/profile", { general: general.value.trim() || null, skills: [...rows.children].map((r) => r.read()),
    desiredSalaryRub: desiredSalary.value ? Number(desiredSalary.value) : null,
    readyForTestTask: readyForTest.value === "" ? null : readyForTest.value === "true" }),
  (saved) => { render(); return `Профиль кандидата сохранён: навыков ${saved.skills.length}`; }) },
  el("h2", { textContent: "Профиль кандидата" }),
  el("p", { className: "muted", textContent: "Общий для всех резюме. В письмо, оценку, ответы анкет и разбор ответов LLM получает общую часть и факты только по навыкам вакансии. Пишите только то, что было: это уходит работодателю от вашего имени." }),
  Field("Общая часть", general, { stacked: true }),
  Field("Желаемая зарплата на руки, ₽", desiredSalary, { stacked: true }),
  Field("Готов к тестовому заданию", readyForTest, { stacked: true }),
  el("p", { className: "muted", textContent: "Эти два поля LLM не получает: сервер дописывает их к письму, только если работодатель просит. Пусто — отклик с такой просьбой уйдёт на проверку." }),
  el("label", { className: "field", textContent: `Факты по навыкам (${profile.skills.length})` }), rows,
  readOnly() ? null : Actions([add, save]));
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

  const save = Button(r ? "Сохранить" : "Добавить", { kind: "primary", type: "submit" });
  return Form({ submit: save, onSubmit: () => act(() => put("/api/resumes", {
    name: inputs.name.value.trim(), match: inputs.match.value.trim(), keywords: lines(inputs.keywords.value),
    summary: inputs.summary.value.trim() || null, letterTemplate: inputs.letterTemplate.value.trim() || null,
  }), (saved) => { render(); return `Резюме «${saved.name}» сохранено`; }) },
  el("h2", { textContent: r ? r.name : "Новый профиль резюме" }),
  FormGrid(
    Field("Название", inputs.name), Field("Совпадение", inputs.match), Field("Ключевые слова", inputs.keywords),
    Field("Описание для LLM", inputs.summary, { wide: true }), Field("Шаблон письма", inputs.letterTemplate, { wide: true })),
  readOnly() ? null : Toolbar([save], { after: true }));
}

async function account() {
  const [tokens, telegram] = await Promise.all([api("/api/tokens"), api("/api/telegram")]);

  // Пароль
  const current = el("input", { type: "password", autocomplete: "current-password", required: true });
  const fresh = el("input", { type: "password", autocomplete: "new-password", required: true, minLength: 10 });
  const change = Button("Сменить пароль", { kind: "primary", type: "submit" });
  const passwordForm = Form({ submit: change, onSubmit: () => act(() => post("/api/auth/password", { currentPassword: current.value, newPassword: fresh.value }),
    () => { current.value = fresh.value = ""; return "Пароль изменён"; }) },
  el("h2", { textContent: "Пароль" }),
  FormGrid(Field("Текущий", current), Field("Новый", fresh)),
  Toolbar([change], { after: true }),
  el("p", { className: "muted", textContent: "Остальные сессии будут завершены." }));

  // Персональные токены — для расширения
  const tokenName = el("input", { placeholder: "Название, например «ноутбук»", "aria-label": "Название токена", required: true });
  const issue = Button("Выпустить", { kind: "primary", type: "submit" });
  const issued = el("div");
  const tokenForm = Form({ className: "bar", submit: issue, onSubmit: async () => {
    const token = await act(() => post("/api/tokens", { name: tokenName.value.trim() }), "Токен выпущен");
    if (!token) return;
    tokenName.value = "";
    tokensTable.replaceWith(tokensTable = tokenRows(await api("/api/tokens")));
    issued.replaceChildren(el("p", { textContent: "Скопируйте токен в панель расширения — он показывается один раз:" }), el("div", { className: "secret", textContent: token.value }));
  } }, tokenName, issue);
  let tokensTable = tokenRows(tokens);

  function tokenRows(items) {
    if (!items.length) return el("p", { className: "muted", textContent: "Токенов нет." });
    return StackTable([{ title: "Название" }, { title: "Начало" }, { title: "Выпущен" }, { title: "" }],
      items.map((t) => {
        const revoke = Button("Отозвать", { kind: "danger", onClick: () => {
          if (confirm(`Отозвать токен «${t.name}»? Расширение с ним перестанет работать.`))
            return act(() => api(`/api/tokens/${t.id}`, { method: "DELETE" }), () => { render(); return `Токен «${t.name}» отозван`; });
        } });
        return [t.name, el("span", { className: "mono", textContent: `${t.prefix}…` }), date(t.createdAt), t.revokedAt ? `отозван ${date(t.revokedAt)}` : revoke];
      }));
  }

  // Telegram
  function telegramView(t) {
    if (!t.configured) return el("p", { className: "muted", textContent: "Бот не настроен на сервере (TELEGRAM_BOT_TOKEN)." });
    if (t.linked) {
      const unlink = Button("Отвязать", { kind: "danger", onClick: () => act(() => api("/api/telegram", { method: "DELETE" }), () => { render(); return "Telegram отвязан"; }) });
      return Toolbar([`Привязан${t.username ? ` к @${t.username}` : ""} ${date(t.linkedAt)}`, unlink]);
    }
    const out = el("div");
    const link = Button("Получить ссылку привязки", { kind: "primary", onClick: async () => {
      const code = await act(() => post("/api/telegram/link"), "Код действует 15 минут");
      if (!code) return;
      out.replaceChildren(code.link
        ? el("p", {}, "Откройте ", el("a", { href: safeUrl(code.link), target: "_blank", rel: "noopener", textContent: "ссылку на бота" }), " и нажмите «Start».")
        : el("p", {}, "Отправьте боту: ", Secret(`/start ${code.code}`)));
    } });
    return el("div", {}, Toolbar(["Не привязан", link]), out);
  }

  return [
    PageHead("Профиль", `${me.name}, роль ${me.role}`),
    me.viaApiKey ? el("p", { className: "card", textContent: "Вход по токену: пароль, токены и Telegram меняются только после входа по паролю." }) : passwordForm,
    Card({ title: "Токены расширения" }, tokenForm, issued, tokensTable),
    Card({ title: "Telegram" }, el("div", {}, telegramView(telegram))),
    Card({ title: "Отчёт" }, el("a", { href: "/api/report?format=csv", textContent: "Скачать отчёт по откликам (CSV)" })),
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

  const button = Button("Собрать вакансии", { kind: "primary", disabled: true, onClick: async () => {
    await act(() => post("/api/collect/start"), "Сбор запущен");
    refresh();
  } });
  refresh();

  return Card({ title: "Сбор вакансий" },
    el("p", { className: "muted", textContent: "Идёт сам раз в час. Запущенный сбор продолжается на сервере, страницу можно обновлять и закрывать." }),
    text, Toolbar([button], { flush: true }));
}

const ROLES = [["User", "User"], ["Admin", "Admin"]];

async function admin() {
  if (me.role !== "Admin") return [EmptyState("Раздел только для администратора.")];
  const users = await api("/api/users");

  const rows = users.map((u) => {
    const self = u.id === me.id;
    const role = el("select", { disabled: self, "aria-label": "Роль" }, options(ROLES));
    role.value = u.role;
    role.onchange = () => busy(role, () => act(() => put(`/api/users/${u.id}/role`, { role: role.value }), `${u.email}: роль ${role.value}`));
    const block = Button(u.blocked ? "Разблокировать" : "Заблокировать", { kind: u.blocked ? null : "danger", disabled: self,
      onClick: () => act(() => put(`/api/users/${u.id}/blocked`, { blocked: !u.blocked }), () => { render(); return `${u.email}: ${u.blocked ? "разблокирован" : "заблокирован"}`; }) });
    const reset = Button("Задать пароль", { onClick: () => {
      const password = prompt(`Новый пароль для ${u.email} (не короче 10 символов)`);
      if (password) return act(() => put(`/api/users/${u.id}/password`, { password }), `${u.email}: пароль задан`);
    } });
    return [u.email, role, date(u.createdAt), Actions([block, reset], { flush: true })];
  });

  const email = el("input", { type: "email", placeholder: "Email", "aria-label": "Email", required: true });
  const password = el("input", { type: "password", placeholder: "Пароль", "aria-label": "Пароль", autocomplete: "new-password", required: true, minLength: 10 });
  const newRole = el("select", { "aria-label": "Роль" }, options(ROLES));
  const create = Button("Создать", { kind: "primary", type: "submit" });
  const createForm = Form({ className: "bar", submit: create, onSubmit: () => act(() => post("/api/users", { email: email.value.trim(), password: password.value, role: newRole.value }),
    () => { render(); loadUsers(); return `Пользователь ${email.value.trim()} создан`; }) }, email, password, newRole, create);

  // Ручной запуск фоновых проходов: обычно они идут сами (сбор — раз в час, LLM — раз в минуту при наличии работы)
  const jobs = [
    ["Проверить LLM", "/api/llm/check", undefined, (r) => `LLM ${r.model}: ответ за ${r.latencyMs} мс`],
    ["Оценить ожидающие", "/api/llm/evaluate", { limit: 20 }, (r) => `Оценено ${r.evaluated}: подходят ${r.fit}, сомнительных ${r.doubtful}, отклонено ${r.rejected}; осталось ${r.remaining}`],
    ["Ответить на анкеты", "/api/llm/answers", { limit: 20 }, (r) => `Анкеты: отвечено ${r.answered}, не вышло ${r.failed}; осталось ${r.remaining}`],
    ["Написать письма", "/api/llm/letters", { limit: 20 }, (r) => `Письма: написано ${r.written}, не вышло ${r.failed}; осталось ${r.remaining}`],
  ].map(([text, path, body, done]) => Button(text, { onClick: () => { setStatus(`${text}…`); return act(() => post(path, body), done); } }));

  return [
    el("h1", { textContent: "Администрирование" }),
    Card({ title: "Пользователи" },
      StackTable([{ title: "Email" }, { title: "Роль" }, { title: "Создан" }, { title: "" }], rows),
      el("h2", { textContent: "Новый пользователь", style: "margin-top:16px" }), createForm),
    collectionCard(),
    Card({ title: "Запустить сейчас" },
      el("p", { className: "muted", textContent: "Обычно не нужно: LLM работает раз в минуту, если есть работа. Тратит лимит LLM." }),
      Toolbar(jobs, { flush: true })),
  ];
}

// ── Каркас ───────────────────────────────────────────────────────────

const routes = { dashboard, overview, vacancies, review, dialogs, searches, resumes, account, admin, kit };

// Разделы (#120): tab — место в нижней панели телефона (остальные — в «Ещё»), icon — значок там же.
// Витрина #kit (#129) в навигации не показывается: только по адресу, только администратору
const SECTIONS = [
  { route: "dashboard", title: "Дашборд", icon: "dashboard", tab: true },
  { route: "review", title: "Проверка", icon: "review", tab: true, count: "review" },
  { route: "dialogs", title: "Ответы", icon: "dialogs", tab: true, count: "dialogs" },
  { route: "vacancies", title: "Вакансии", icon: "vacancies", tab: true },
  { route: "overview", title: "Обзор" },
  { route: "searches", title: "Поиски" },
  { route: "resumes", title: "Резюме" },
  { route: "account", title: "Профиль" },
  { route: "admin", title: "Администрирование", admin: true },
];
const counts = { review: 0, dialogs: 0 };
const tabbar = document.getElementById("tabbar");
const more = Sheet([], { label: "Ещё разделы" });
more.id = "more";
document.body.append(more);

function buildNav(route) {
  const visible = SECTIONS.filter((s) => !s.admin || me?.role === "Admin");
  const item = (s) => ({ href: `#${s.route}`, label: s.title, icon: s.icon, active: s.route === route, count: s.count ? counts[s.count] : 0 });
  nav.replaceChildren(Tabs(visible.map(item)));
  const rest = visible.filter((s) => !s.tab);
  const moreButton = el("button", { type: "button", className: rest.some((s) => s.route === route) ? "active" : "", "aria-label": "Ещё разделы" }, Icon("more"), "Ещё");
  moreButton.onclick = (e) => { e.stopPropagation(); more.toggle(); };
  tabbar.replaceChildren(...TabBar(visible.filter((s) => s.tab).map(item), moreButton));
  more.replaceChildren(...SheetLinks(rest.map(item)));
  more.close();
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

// Тема: системная → тёмная → светлая; выбор — в localStorage (может быть недоступен — тогда только на эту вкладку)
const THEMES = [["system", "theme", "Тема: как в системе"], ["dark", "moon", "Тема: тёмная"], ["light", "sun", "Тема: светлая"]];
const themeButton = document.getElementById("theme");
function applyTheme(name) {
  if (name === "system") delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = name;
  const [, icon, title] = THEMES.find(([n]) => n === name);
  themeButton.replaceChildren(Icon(icon));
  themeButton.title = title;
  themeButton.setAttribute("aria-label", title);
}
function setTheme(name) {
  try { name === "system" ? localStorage.removeItem("theme") : localStorage.setItem("theme", name); } catch {}
  applyTheme(name);
}
themeButton.onclick = () => {
  const now = document.documentElement.dataset.theme ?? "system";
  setTheme(THEMES[(THEMES.findIndex(([n]) => n === now) + 1) % THEMES.length][0]);
};
applyTheme(document.documentElement.dataset.theme ?? "system");

// ── Витрина UI kit (#129) ────────────────────────────────────────────
// Все компоненты во всех состояниях на выдуманных данных: по ней проверяются тема и телефон, из неё собираются концепты.
// #kit/frame — та же витрина без переключателей (для просмотра в рамке заданной ширины)
const kitState = { themes: "one", width: "auto" };

function kitGallery() {
  const sec = (title, ...children) => el("section", { className: "kit-sec" }, el("h2", { textContent: title }), children);
  const row = (label, ...children) => el("div", { className: "kit-row" }, el("span", { className: "muted", textContent: label }), children);
  const wait = (ms = 1500) => () => new Promise((r) => setTimeout(r, ms));
  const busyButton = Button("Сохраняю…", { kind: "primary" });
  busyButton.disabled = true;
  busyButton.setAttribute("aria-busy", "true");
  const tokens = ["--bg", "--card", "--card2", "--text", "--muted", "--border", "--accent", "--accent-soft", "--ok", "--bad", "--warn", "--warn-bg", "--bar", "--track"];
  const segValue = { v: 30 };
  const chipValue = { v: "hh" };
  const sheet = Sheet(SheetLinks([{ href: "#kit", label: "Обзор", icon: "overview", active: true }, { href: "#kit", label: "Поиски", icon: "search" }, { href: "#kit", label: "Резюме", icon: "resume" }]), { title: "Нижний лист" });
  sheet.classList.add("kit-demo");
  document.body.append(sheet);
  const sheetButton = Button("Открыть лист", { icon: "more", onClick: (e) => { e.stopPropagation(); sheet.toggle(); } });

  return el("div", {},
    sec("Токены",
      el("div", { className: "kit-swatches" }, tokens.map((t) => el("div", {}, el("i", { style: `background:var(${t})` }), t))),
      el("p", { className: "muted small", textContent: "Шрифты: --sans (текст), --mono (числа, метки, логотип). Радиусы --r-xs…--r-xl, отступы --sp-1…--sp-6 (шаг 4px), касание --tap 44px." })),
    sec("Иконки", el("div", { className: "kit-icons" }, ICONS.map((name) => el("div", {}, Icon(name), name)))),
    sec("Button",
      row("виды", Button("Обычная"), Button("Primary", { kind: "primary" }), Button("Ok", { kind: "ok" }), Button("Danger", { kind: "danger" }), Button("Ghost", { kind: "ghost" })),
      row("размеры", Button("sm", { size: "sm" }), Button("обычная"), Button("lg", { size: "lg", kind: "primary" })),
      row("с иконкой", Button("Скопировать", { icon: "copy" }), Button("Вернуть", { icon: "undo", kind: "ghost" }), Button("Тема", { icon: "theme", iconOnly: true }), Button("Ещё", { icon: "more", iconOnly: true })),
      row("состояния", Button("Выключена", { disabled: true }), Button("Primary выкл.", { kind: "primary", disabled: true }), busyButton,
        Button("Нажмите — занята 1,5 с", { kind: "primary", onClick: wait() }))),
    sec("Pill, Badge, Count",
      row("Pill", Pill("в очереди"), Pill("приглашение", { tone: "ok" }), Pill("ошибка", { tone: "bad" }), Pill("на проверке", { tone: "accent" }), Pill("hh.ru", { dot: "var(--src-hh)" }), Pill("hirify", { dot: "var(--src-hirify)" })),
      row("Badge", Badge("Приглашение", { tone: "ok" }), Badge("Вопрос", { tone: "accent" }), Badge("Отказ", { tone: "bad" }), Badge("Прочее")),
      row("Count", el("span", {}, "Проверка", Count(7)), el("span", {}, "Ответы", Count(120)))),
    sec("Card",
      el("div", { className: "cols" },
        Card({ title: "C# разработчик (выдуманная)", href: "https://example.com/vacancy/1", meta: ["ООО «Пример»", "Москва", "от 250 000 RUR", "удалённо", "балл 82"],
          actions: [Button("В очередь", { kind: "primary" }), Button("Скрыть"), Result()] },
        Callout("LLM: стек совпадает, но в тексте — 5 лет опыта, у кандидата 3."), Disclosure("Описание вакансии", Pre("Описание вакансии с переносами строк.\nВторая строка."))),
        Card({ title: "Решённая карточка", meta: "одобрено, в очереди", decided: true, actions: [Result("одобрено, в очереди")] }, Quote("Здравствуйте! Приглашаем на собеседование в четверг."))),
      Card({ tight: true }, "Плотная карточка (tight) — строка навыка в профиле.")),
    sec("Tabs, Segmented, Chips",
      Tabs([{ href: "#kit", label: "Дашборд", active: true }, { href: "#kit", label: "Проверка", count: 7 }, { href: "#kit", label: "Ответы", count: 4 }, { href: "#kit", label: "Вакансии" }], { label: "Пример вкладок" }),
      el("div", { style: "height:12px" }),
      row("Segmented", Segmented(PERIODS.map((p) => ({ value: p, label: `${p} дн` })), segValue.v, (v) => { segValue.v = v; }, { label: "Период" })),
      Chips(AGGREGATORS, chipValue.v, (v) => { chipValue.v = v; }, { label: "Агрегатор" })),
    sec("Stat",
      Stats([
        Stat({ value: 128, label: "собрано вакансий", note: "+24% к прошлым 30 дн", trend: "up" }),
        Stat({ value: 31, label: "откликов отправлено", note: "-12% к прошлым 30 дн", trend: "down" }),
        Stat({ value: 9, unit: "%", label: "в приглашение", note: "3 приглашения за период" }),
        Stat({ value: 0, label: "отказов", note: "—" }),
      ]),
      Stats([Stat({ value: 12, label: "в очереди", href: "#kit" }), Stat({ value: 3, label: "на проверке", href: "#kit" }), Stat({ value: 40, label: "пропущена", href: "#kit" })], { tiles: true })),
    sec("Meter, Funnel, Bars",
      el("div", { className: "cols" },
        Card({}, Caption("Meter"), row("70%", Meter(70, 100)), row("ok", Meter(3, 10, { color: "var(--ok)" })), row("bad", Meter(1, 10, { color: "var(--bad)" })), row("0", Meter(0, 10)),
          Caption("Funnel"), Funnel([{ label: "Собрано", value: 128, base: null }, { label: "Оценено LLM", value: 96, base: 128 }, { label: "Отправлено", value: 31, base: 96 },
            { label: "Приглашение", value: 3, base: 31, color: "var(--ok)" }, { label: "Отказ", value: 7, base: 31, color: "var(--bad)" }], { note: "% — от предыдущего шага" })),
        Card({}, Caption("Bars"), Bars(Array.from({ length: 14 }, (_, i) => {
          const sent = [2, 4, 1, 0, 5, 3, 6, 2, 0, 1, 4, 7, 3, 2][i];
          const answered = Math.min(sent, [1, 1, 0, 0, 2, 1, 3, 0, 0, 1, 2, 2, 1, 1][i]);
          const inv = i % 5 === 0 ? Math.min(1, answered) : 0;
          return { total: Math.max(sent, answered), title: `день ${i + 1}: отправлено ${sent}`, parts: [{ value: inv, tone: "ok" }, { value: answered - inv, tone: "accent" }] };
        }), { from: "5 нояб.", to: "сегодня", legend: [{ label: "отправлено", color: "var(--bar)" }, { label: "ответили отказом", color: "var(--accent)" }, { label: "приглашение", color: "var(--ok)" }] }),
        Caption("RankList"), RankList([["оценка N ниже N", 5], ["стоп-слово «…»", 3], ["LLM", 1]])))),
    sec("StackTable",
      Card({}, StackTable([{ title: "Поиск" }, { title: "Собрано", num: true }, { title: "Отправлено", num: true }, { title: "Конверсия", num: true }, { title: "" }],
        [["dotnet-remote", 20, 3, "15%", Button("Открыть", { size: "sm" })], ["backend-msk", 3, 0, "0%", Button("Открыть", { size: "sm" })]]),
      el("p", { className: "muted small", textContent: "На ширине ≤ 720px строки становятся карточками «поле: значение»." }))),
    sec("Field, Form",
      Form({ onSubmit: () => Toast("Форма отправлена (витрина: запроса нет)", { tone: "ok" }), submit: null },
        FormGrid(Field("Название", el("input", { value: "dotnet-remote" })), Field("Запрос", el("input", { placeholder: "C# OR .NET" })),
          Field("Включён", el("input", { type: "checkbox", checked: true })), Field("Выключено", el("input", { value: "только чтение", disabled: true })),
          Field("Формат", el("select", {}, options([["", "любой"], ["remote", "удалённо"]]))), Field("Описание (wide)", el("textarea", { value: "Поле на всю ширину" }), { wide: true })),
        Field("Подпись над полем (stacked)", el("textarea", { placeholder: "Черновик ответа" }), { stacked: true }),
        Actions([el("input", { placeholder: "Причина (необязательно)", "aria-label": "Причина" }), Button("Сохранить", { kind: "primary", type: "submit" }), Result("сохранено")]))),
    sec("Тексты",
      Callout("Callout — вывод LLM или итог разбора."), Quote("Quote — сообщение работодателя как есть."), Note("Note — служебная пометка (демо, задел)."),
      el("p", {}, "Secret: ", Secret("hh_pat_xxxxxxxx")),
      Disclosure("Disclosure — раскрывающийся блок", Pre("Pre — длинный текст\nс переносами строк.")), Meta(["Meta", "части через точку", "пустые пропускаются", null])),
    sec("Toast, Sheet",
      row("Toast", Button("Итог", { onClick: () => { Toast("«C# разработчик»: в очереди", { tone: "ok" }); } }),
        Button("С «Отменить»", { onClick: () => { Toast("Вакансия скрыта", { undo: () => Toast("Возвращена") }); } }),
        Button("Ошибка", { kind: "danger", onClick: () => { Toast("Сервер ответил 500", { tone: "bad" }); } })),
      row("Sheet", sheetButton)),
    sec("EmptyState, Skeleton",
      el("div", { className: "cols" },
        Card({}, EmptyState("На проверке ничего нет.", { icon: "check" }), EmptyState("Без иконки — как в разделах.")),
        Card({}, Skeleton({ block: true, lines: 3 })))));
}

async function kit(arg) {
  if (me.role !== "Admin") return [EmptyState("Раздел только для администратора.")];
  if (arg === "frame") return [PageHead("UI kit", "Витрина в рамке"), kitGallery()];

  const body = el("div");
  function draw() {
    // Листы прошлой отрисовки витрины — прочь
    document.querySelectorAll(".sheet.kit-demo").forEach((n) => n.remove());
    if (kitState.width !== "auto") {
      body.replaceChildren(el("iframe", { className: "kit-frame", src: "#kit/frame", title: `Витрина шириной ${kitState.width}px`, style: `max-width:${kitState.width}px` }));
      return;
    }
    body.replaceChildren(kitState.themes === "both"
      ? el("div", { className: "kit-themes" }, ["dark", "light"].map((t) => el("div", { "data-theme": t }, el("h3", { textContent: t === "dark" ? "Тёмная" : "Светлая" }), kitGallery())))
      : kitGallery());
  }
  const aside = el("div", { className: "bar flush" },
    Segmented([{ value: "system", label: "система" }, { value: "dark", label: "тёмная" }, { value: "light", label: "светлая" }],
      document.documentElement.dataset.theme ?? "system", setTheme, { label: "Тема" }),
    Segmented([{ value: "one", label: "одна" }, { value: "both", label: "обе рядом" }], kitState.themes, (v) => { kitState.themes = v; draw(); }, { label: "Темы" }),
    Segmented([{ value: "auto", label: "окно" }, { value: 375, label: "375" }, { value: 720, label: "720" }], kitState.width, (v) => { kitState.width = v; draw(); }, { label: "Ширина" }));
  draw();
  return [PageHead("UI kit", "Компоненты ui/ui.js во всех состояниях · данные выдуманные · только для администратора", aside), body];
}

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
