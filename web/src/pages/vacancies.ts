// Вакансии: виды-вкладки (В очереди · Отправлено · Приглашения · Отказы · Все), фильтры чипами и решения по вакансии
// вне ручной проверки. Всё состояние списка — в адресе (/vacancies?view=queued&search=…): ссылку можно открыть с телефона (#128)
import { api, post } from "../api/http";
import type { LetterRequest, LlmConnection, LlmProviders, Search, Vacancy } from "../api/types";
import { connectionState } from "./llm";
import { date, salary, sourcePill, STATUSES } from "../format";
import { path, type Route } from "../router";
import { act, app, readOnly, scoped } from "../state";
import { Actions, Button, Card, Chips, Disclosure, el, EmptyState, Menu, Meta, PageHead, Pill, Pre, Result, safeUrl, StackTable, Tabs, Toast, Toolbar, type Child } from "../ui/ui";

// Подключения LLM сервера — для «Оценить заново через» на карточках текущего списка
let connections: LlmConnection[] = [];
import { undoable } from "../undo";

// Виды списка: статус вида — фильтр запроса; «Все» — любой статус, остальные статусы выбираются чипами
export const VIEWS = [
  { id: "queued", label: "В очереди", status: "Queued" },
  { id: "applied", label: "Отправлено", status: "Applied" },
  { id: "invited", label: "Приглашения", status: "Invited" },
  { id: "interview", label: "Собеседования", status: "Interview" },
  { id: "rejected", label: "Отказы", status: "Rejected" },
  { id: "all", label: "Все", status: "" },
] as const;
export type ViewId = (typeof VIEWS)[number]["id"];

export interface VacancyFilter {
  view: ViewId;
  // Статус в виде «Все» (пусто — любой)
  status: string;
  // Имя поиска (пусто — все)
  search: string;
  hidden: boolean;
  skip: number;
}

export const VACANCY_PAGE = 50;
const isView = (id: string | null): id is ViewId => VIEWS.some((v) => v.id === id);

// Адрес → фильтр. Старые ссылки ?status=X (дашборд, «Требует внимания», #vacancies/X) — вид этого статуса или «Все» со статусом.
// Без параметров — «В очереди»; неизвестные значения отбрасываются
export function parseVacancyFilter(query: URLSearchParams): VacancyFilter {
  const raw = query.get("status") ?? "";
  const status = raw in STATUSES ? raw : "";
  const viewParam = query.get("view");
  const view: ViewId = isView(viewParam) ? viewParam : status ? (VIEWS.find((v) => v.status === status)?.id ?? "all") : "queued";
  const skip = Math.max(0, Math.floor(Number(query.get("skip")) || 0));
  return { view, status: view === "all" ? status : "", search: query.get("search") ?? "", hidden: ["1", "true"].includes(query.get("hidden") ?? ""), skip };
}

// Фильтр → адрес раздела: только непустые части, статус — только в виде «Все»
export function vacancyFilterPath(f: VacancyFilter): string {
  const query = new URLSearchParams({ view: f.view });
  if (f.view === "all" && f.status) query.set("status", f.status);
  if (f.search) query.set("search", f.search);
  if (f.hidden) query.set("hidden", "1");
  if (f.skip > 0) query.set("skip", String(f.skip));
  return `/vacancies?${query}`;
}

// Фильтр → запрос к API
export function vacancyApiQuery(f: VacancyFilter, take = VACANCY_PAGE): URLSearchParams {
  const query = new URLSearchParams({ skip: String(f.skip), take: String(take) });
  const status = VIEWS.find((v) => v.id === f.view)?.status || f.status;
  if (status) query.set("status", status);
  if (f.search) query.set("search", f.search);
  if (f.hidden) query.set("includeHidden", "true");
  return query;
}

export async function vacancies(route: Route): Promise<Child> {
  const f = parseVacancyFilter(route.query);
  const [searches, items, providers] = await Promise.all([
    api<Search[]>(scoped("/api/searches")),
    api<Vacancy[]>(scoped(`/api/vacancies?${vacancyApiQuery(f)}`)),
    // Подключения для «Оценить заново через» (#107); недоступен список — кнопки нет, остальное работает
    api<LlmProviders>("/api/llm/providers").catch(() => null),
  ]);
  connections = providers?.providers ?? [];
  // Смена вида и страницы — новая запись в истории («назад» возвращает), фильтры — на месте текущей
  const go = (next: Partial<VacancyFilter>, replace = true) => app.navigate(vacancyFilterPath({ ...f, skip: 0, ...next }), { replace });

  const views = Tabs(VIEWS.map((v) => ({ href: vacancyFilterPath({ ...f, view: v.id, status: "", skip: 0 }), label: v.label, active: v.id === f.view })), { label: "Виды" });
  views.classList.add("views");
  const chips = el("div", { className: "filters" },
    f.view === "all" ? Chips([{ value: "", label: "любой" }, ...Object.entries(STATUSES).map(([value, label]) => ({ value, label }))], f.status,
      (status) => go({ status }), { label: "Статус" }) : null,
    searches.length ? Chips([{ value: "", label: "все" }, ...searches.map((s) => ({ value: s.name, label: s.name }))], f.search,
      (search) => go({ search }), { label: "Поиск" }) : null,
    Chips([{ value: false, label: "без скрытых" }, { value: true, label: "со скрытыми" }], f.hidden, (hidden) => go({ hidden }), { label: "Скрытые" }));

  const pager = items.length || f.skip ? Toolbar([
    Button("← Назад", { disabled: f.skip === 0, onClick: () => go({ skip: Math.max(0, f.skip - VACANCY_PAGE) }, false) }),
    Button("Дальше →", { disabled: items.length < VACANCY_PAGE, onClick: () => go({ skip: f.skip + VACANCY_PAGE }, false) }),
    el("span", { className: "muted", textContent: items.length ? `${f.skip + 1}–${f.skip + items.length}` : "" }),
  ], { after: true }) : null;

  return [PageHead("Вакансии"), views, chips,
    el("div", {}, items.length ? items.map(vacancyCard) : EmptyState("Ничего нет.")), pager];
}

// Решения по вакансии вне ручной проверки: пропущенную или упавшую — снова в очередь, ожидающую — пропустить (забыть).
// Обратимые (#128) — сразу и с «Отменить» в тосте: скрыть ↔ вернуть, пропустить из очереди ↔ в очередь, пропущенную в очередь ↔ снова пропустить.
// Без обратного в API — тост без отмены: из «ошибки» в очередь (вернуть «ошибку» нельзя), пропуск ожидающей оценки (вернётся в очередь, а не на оценку)
const REQUEUE = ["Skipped", "Failed"];
const SKIPPABLE = ["Queued", "Pending", "Questionnaire", "New"];
export const vacancyPath = (v: Vacancy, action: string) => `/api/vacancies/${encodeURIComponent(v.id)}/${action}`;

function vacancyActions(v: Vacancy, pill: HTMLElement): HTMLElement | null {
  if (readOnly()) return null;
  if (v.status === "Review") return Actions([el("a", { href: path("review"), textContent: "Решить в «Проверке» →" })]);

  const result = Result();
  // Итог на месте кнопок; карточка приглушается («решено»), статус в шапке — новый
  const finish = (text: string, status?: string) => {
    Card.decide(result);
    result.textContent = text;
    if (status) pill.textContent = STATUSES[status];
    return `«${v.title}»: ${status ? STATUSES[status] : text}`;
  };
  // После отмены — карточка как была (до решения)
  const restore = () => result.closest(".card")?.replaceWith(vacancyCard(v));
  // Без обратного действия — тот же итог, тост без «Отменить»
  const once = (run: () => Promise<unknown>, done: () => string) => act(run).then((r) => { if (r !== undefined) Toast(done(), { tone: "ok" }); });
  const undoTo = (text: string) => `«${v.title}»: ${text}`;

  if (v.hiddenAt) {
    const unhide = Button("Вернуть", { onClick: () => undoable(() => post(vacancyPath(v, "unhide")), {
      done: () => { unhide.remove(); return finish("возвращена в список"); },
      undo: () => post(vacancyPath(v, "hide")), undone: undoTo("снова скрыта"), onUndone: restore }) });
    return Actions([unhide, result]);
  }

  if (REQUEUE.includes(v.status)) {
    // Оценить заново и скрыть — только отсеянные: у Failed отклик мог уйти на hh.ru
    const clear = () => [requeue, reevaluate, through, hide].forEach((b) => b?.remove());
    const requeue = Button("В очередь", { kind: "primary", onClick: () => {
      const run = () => post(vacancyPath(v, "status"), { status: "Queued" });
      const done = () => { clear(); return finish("в очереди", "Queued"); };
      if (v.status !== "Skipped") return once(run, done);
      return undoable(run, { done, undo: () => post(vacancyPath(v, "status"), { status: "Skipped", reason: v.statusReason ?? "пропущено вручную" }),
        undone: undoTo("снова пропущена"), onUndone: restore });
    } });
    const reevaluated = (r: Vacancy) => {
      clear();
      return finish(r.status === "Skipped" ? `снова отсеяна: ${r.statusReason ?? ""}` : STATUSES[r.status], r.status);
    };
    const reevaluate = v.dismissed ? Button("Оценить заново", {
      onClick: () => act(() => post<Vacancy>(vacancyPath(v, "reevaluate")), reevaluated) }) : null;
    // Разовый прогон другой моделью (#107): оценка сразу выбранным подключением, настройка «Мои модели» не меняется
    const through = v.dismissed && connections.length ? Menu("Оценить заново через", connections.map((c) => ({
      label: `${c.name} · ${c.model}${c.available ? "" : ` — ${connectionState(c)}`}`,
      onClick: () => act(() => post<Vacancy>(`${vacancyPath(v, "reevaluate")}?provider=${encodeURIComponent(c.name)}`), reevaluated),
    })), { label: "Оценить заново через подключение", align: "start", button: true }) : null;
    const hide = v.dismissed ? Button("Скрыть", { onClick: () => undoable(() => post(vacancyPath(v, "hide")), {
      done: () => { clear(); return finish("скрыта"); },
      undo: () => post(vacancyPath(v, "unhide")), undone: undoTo("возвращена"), onUndone: restore }) }) : null;
    return Actions([requeue, reevaluate, through, hide, result]);
  }

  if (SKIPPABLE.includes(v.status)) {
    const reason = el("input", { placeholder: "Причина (необязательно)", "aria-label": "Причина пропуска" });
    const skip = Button("Пропустить", { kind: "danger", onClick: () => {
      const run = () => post(vacancyPath(v, "status"),
        { status: "Skipped", reason: reason.value.trim() ? `пропущено вручную: ${reason.value.trim()}` : "пропущено вручную" });
      const done = () => { skip.remove(); reason.remove(); return finish("пропущена", "Skipped"); };
      if (v.status !== "Queued") return once(run, done);
      return undoable(run, { done, undo: () => post(vacancyPath(v, "status"), { status: "Queued" }), undone: undoTo("снова в очереди"), onUndone: restore });
    } });
    return Actions([reason, skip, result]);
  }
  return null;
}
// Просьбы работодателя к письму (#72). Цитата — только для чтения: в письмо идёт лишь проверенное значение
const LETTER_REQUESTS: Record<string, string> = { Arithmetic: "Пример", Salary: "Зарплата", TestTask: "Тестовое", CodeWord: "Кодовое слово", Other: "Прочее" };

export function letterRequestLine(r: LetterRequest): HTMLElement {
  const state = r.closed ? (r.answer ? `закрыта: ${r.answer}` : "решено человеком") : (r.problem ?? `ответ: ${r.answer} — допишется к письму`);
  return el("div", { className: "reason" }, el("b", { textContent: LETTER_REQUESTS[r.type] ?? r.type }), r.text ? ` — ${r.text}` : "", el("br"), state);
}

export function letterRequestsBlock(v: Vacancy): HTMLElement | null {
  if (v.letterRequestsState === "Failed") return el("div", { className: "reason", textContent: "Просьбы работодателя к письму разобрать не удалось: прочитайте вакансию и напишите письмо сами." });
  return v.letterRequests?.length ? Disclosure(`Просьбы к письму (${v.letterRequests.length})`, v.letterRequests.map(letterRequestLine), { open: v.letterRequestsState === "Found" }) : null;
}

function vacancyCard(v: Vacancy): HTMLElement {
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
