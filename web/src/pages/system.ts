// Система (#128, бывшее «Администрирование»): пользователи и сбор вакансий; подключения и запуск задач LLM — во вкладке LLM (#127)
import { api, post, put, Unauthorized } from "../api/http";
import type { CollectCounts, CollectState, User } from "../api/types";
import { date, options } from "../format";
import { path } from "../router";
import { act, app, me, page as pageState } from "../state";
import { undoable } from "../undo";
import { Actions, busy, Button, Card, Confirm, el, EmptyState, Form, StackTable, Toolbar, type Child } from "../ui/ui";

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


export async function system(): Promise<Child> {
  const user = me();
  if (user.role !== "Admin") return [EmptyState("Раздел только для администратора.")];
  const users = await api<User[]>("/api/users");

  const rows = users.map((u) => {
    const self = u.id === user.id;
    const role = el("select", { disabled: self, "aria-label": "Роль" }, options(ROLES));
    role.value = u.role;
    // Роль меняет права — с подтверждением; отказ или ошибка возвращают прежнее значение
    let currentRole = u.role;
    role.onchange = async () => {
      const next = role.value;
      const sure = await Confirm({ title: `Роль ${next} для ${u.email}?`,
        text: next === "Admin" ? "Администратор видит данные всех пользователей и управляет ими." : "Пользователь потеряет доступ к разделу «Система».", confirm: "Сменить роль" });
      const done = sure && await busy(role, () => act(() => put(`/api/users/${u.id}/role`, { role: next }), `${u.email}: роль ${next}`));
      if (done === undefined || done === false) role.value = currentRole;
      else currentRole = next;
    };
    // Блокировка обратима — сразу, с «Отменить» в тосте
    const block = Button(u.blocked ? "Разблокировать" : "Заблокировать", { kind: u.blocked ? null : "danger", disabled: self,
      onClick: () => undoable(() => put(`/api/users/${u.id}/blocked`, { blocked: !u.blocked }), {
        done: () => { app.render(); return `${u.email}: ${u.blocked ? "разблокирован" : "заблокирован"}`; },
        undo: () => put(`/api/users/${u.id}/blocked`, { blocked: u.blocked }),
        undone: `${u.email}: ${u.blocked ? "снова заблокирован" : "снова разблокирован"}`, onUndone: () => { app.render(); } }) });
    const reset = Button("Задать пароль", { onClick: async () => {
      const field = el("input", { type: "password", required: true, minLength: 10, autocomplete: "new-password", "aria-label": "Новый пароль (не короче 10 символов)" });
      if (await Confirm({ title: `Пароль для ${u.email}`, text: "Старый пароль перестанет работать.", confirm: "Задать", field }))
        return act(() => put(`/api/users/${u.id}/password`, { password: field.value }), `${u.email}: пароль задан`);
    } });
    return [u.email, role, date(u.createdAt), Actions([block, reset], { flush: true })];
  });

  const email = el("input", { type: "email", placeholder: "Email", "aria-label": "Email", required: true });
  const password = el("input", { type: "password", placeholder: "Пароль", "aria-label": "Пароль", autocomplete: "new-password", required: true, minLength: 10 });
  const newRole = el("select", { "aria-label": "Роль" }, options(ROLES));
  const create = Button("Создать", { kind: "primary", type: "submit" });
  const createForm = Form({ className: "bar", submit: create, onSubmit: () => act(() => post("/api/users", { email: email.value.trim(), password: password.value, role: newRole.value }),
    () => { app.render(); app.loadUsers(); return `Пользователь ${email.value.trim()} создан`; }) }, email, password, newRole, create);

  return [
    el("h1", { textContent: "Система" }),
    Card({ title: "Пользователи" },
      StackTable([{ title: "Email" }, { title: "Роль" }, { title: "Создан" }, { title: "" }], rows),
      el("h2", { textContent: "Новый пользователь", style: "margin-top:16px" }), createForm),
    collectionCard(),
    Card({ title: "LLM" },
      el("p", { className: "muted" }, "Подключения, «Проверить» и ручной запуск задач LLM — в ", el("a", { href: path("llm", {}, "connections"), textContent: "«LLM → Подключения»" }), ".")),
  ];
}
