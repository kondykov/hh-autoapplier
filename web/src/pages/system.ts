// Система (#128, бывшее «Администрирование»): пользователи, сбор вакансий, ручной запуск фоновых проходов (до вкладки LLM, #127)
import { api, post, put, Unauthorized } from "../api/http";
import type { CollectCounts, CollectState, User } from "../api/types";
import { date, options } from "../format";
import { act, app, me, page as pageState, setStatus } from "../state";
import { Actions, busy, Button, Card, el, EmptyState, Form, StackTable, Toolbar, type Child } from "../ui/ui";

const collectionCounts = (r: CollectCounts) => `просмотрено ${r.seen}, новых ${r.added}, в очередь ${r.queued}, отсеяно ${r.skipped}`;

export function collectionText(state: CollectState): string {
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
function collectionCard(): HTMLElement {
  const page = pageState.renders;
  const text = el("p", { textContent: "Загрузка состояния…" });
  let timer: ReturnType<typeof setTimeout> | undefined;

  async function refresh(): Promise<void> {
    clearTimeout(timer);
    let state: CollectState;
    try {
      state = await api<CollectState>("/api/collect/status");
    } catch (e) {
      if (e instanceof Unauthorized) return app.showLogin();
      if (page === pageState.renders) timer = setTimeout(refresh, 15000);
      text.textContent = `Состояние сбора недоступно: ${(e as Error).message}`;
      return;
    }
    // Ушли с раздела или он перерисован: опрашивает уже новая карточка
    if (page !== pageState.renders) return;
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

const ROLES: [string, string][] = [["User", "User"], ["Admin", "Admin"]];

type JobResult = Record<string, string | number>;

export async function system(): Promise<Child> {
  const user = me();
  if (user.role !== "Admin") return [EmptyState("Раздел только для администратора.")];
  const users = await api<User[]>("/api/users");

  const rows = users.map((u) => {
    const self = u.id === user.id;
    const role = el("select", { disabled: self, "aria-label": "Роль" }, options(ROLES));
    role.value = u.role;
    role.onchange = () => busy(role, () => act(() => put(`/api/users/${u.id}/role`, { role: role.value }), `${u.email}: роль ${role.value}`));
    const block = Button(u.blocked ? "Разблокировать" : "Заблокировать", { kind: u.blocked ? null : "danger", disabled: self,
      onClick: () => act(() => put(`/api/users/${u.id}/blocked`, { blocked: !u.blocked }), () => { app.render(); return `${u.email}: ${u.blocked ? "разблокирован" : "заблокирован"}`; }) });
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
    () => { app.render(); app.loadUsers(); return `Пользователь ${email.value.trim()} создан`; }) }, email, password, newRole, create);

  // Ручной запуск фоновых проходов: обычно они идут сами (сбор — раз в час, LLM — раз в минуту при наличии работы)
  const jobs: [string, string, object | undefined, (r: JobResult) => string][] = [
    ["Проверить LLM", "/api/llm/check", undefined, (r) => `LLM ${r.model}: ответ за ${r.latencyMs} мс`],
    ["Оценить ожидающие", "/api/llm/evaluate", { limit: 20 }, (r) => `Оценено ${r.evaluated}: подходят ${r.fit}, сомнительных ${r.doubtful}, отклонено ${r.rejected}; осталось ${r.remaining}`],
    ["Ответить на анкеты", "/api/llm/answers", { limit: 20 }, (r) => `Анкеты: отвечено ${r.answered}, не вышло ${r.failed}; осталось ${r.remaining}`],
    ["Написать письма", "/api/llm/letters", { limit: 20 }, (r) => `Письма: написано ${r.written}, не вышло ${r.failed}; осталось ${r.remaining}`],
  ];
  const jobButtons = jobs.map(([text, path, body, done]) => Button(text, { onClick: () => { setStatus(`${text}…`); return act(() => post<JobResult>(path, body), done); } }));

  return [
    el("h1", { textContent: "Система" }),
    Card({ title: "Пользователи" },
      StackTable([{ title: "Email" }, { title: "Роль" }, { title: "Создан" }, { title: "" }], rows),
      el("h2", { textContent: "Новый пользователь", style: "margin-top:16px" }), createForm),
    collectionCard(),
    Card({ title: "Запустить сейчас" },
      el("p", { className: "muted", textContent: "Обычно не нужно: LLM работает раз в минуту, если есть работа. Тратит лимит LLM." }),
      Toolbar(jobButtons, { flush: true })),
  ];
}
