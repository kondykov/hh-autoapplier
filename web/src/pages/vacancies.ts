// Вакансии: список с фильтрами (статус — в адресе: /vacancies?status=Queued) и решения по вакансии вне ручной проверки
import { api, post } from "../api/http";
import type { LetterRequest, Search, Vacancy } from "../api/types";
import { date, options, salary, sourcePill, STATUSES } from "../format";
import { path, type Route } from "../router";
import { act, app, readOnly, scoped, setStatus } from "../state";
import { Actions, Button, Card, Disclosure, el, EmptyState, Meta, Pill, Pre, Result, safeUrl, StackTable, Toolbar, type Child } from "../ui/ui";

export async function vacancies(route: Route): Promise<Child> {
  const take = 50;
  const searches = await api<Search[]>(scoped("/api/searches"));
  const state = { status: route.query.get("status") ?? "", search: route.query.get("search") ?? "", hidden: false, skip: 0 };

  const statusSelect = el("select", { "aria-label": "Статус" }, options([["", "Все статусы"], ...Object.entries(STATUSES)]));
  statusSelect.value = state.status;
  const searchSelect = el("select", { "aria-label": "Поиск" }, options([["", "Все поиски"], ...searches.map((s): [string, string] => [s.name, s.name])]));
  searchSelect.value = state.search;
  const hidden = el("input", { type: "checkbox" });
  const prev = Button("← Назад", { onClick: () => { state.skip = Math.max(0, state.skip - take); load(); } });
  const next = Button("Дальше →", { onClick: () => { state.skip += take; load(); } });
  const page = el("span", { className: "muted" });
  const list = el("div");

  async function load() {
    const query = new URLSearchParams({ skip: String(state.skip), take: String(take) });
    if (state.status) query.set("status", state.status);
    if (state.search) query.set("search", state.search);
    if (state.hidden) query.set("includeHidden", "true");
    try {
      const items = await api<Vacancy[]>(scoped(`/api/vacancies?${query}`));
      list.replaceChildren(...(items.length ? items.map(vacancyCard) : [EmptyState("Ничего нет.")]));
      prev.disabled = state.skip === 0;
      next.disabled = items.length < take;
      page.textContent = items.length ? `${state.skip + 1}–${state.skip + items.length}` : "";
    } catch (e) {
      setStatus((e as Error).message, true);
    }
  }

  // Фильтры — в адресе: обновление страницы и ссылка открывают тот же список
  const remember = () => app.navigate(path("vacancies", { status: state.status, search: state.search }), { replace: true, render: false });
  statusSelect.onchange = () => { state.status = statusSelect.value; state.skip = 0; remember(); load(); };
  searchSelect.onchange = () => { state.search = searchSelect.value; state.skip = 0; remember(); load(); };
  hidden.onchange = () => { state.hidden = hidden.checked; state.skip = 0; load(); };
  await load();

  return [el("h1", { textContent: "Вакансии" }), Toolbar([statusSelect, searchSelect, el("label", {}, hidden, " Показать скрытые"), prev, next, page]), list];
}

// Решения по вакансии вне ручной проверки: пропущенную или упавшую — снова в очередь, ожидающую — пропустить (забыть)
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
      onClick: () => act(() => post<Vacancy>(vacancyPath(v, "reevaluate")), (r) => {
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
