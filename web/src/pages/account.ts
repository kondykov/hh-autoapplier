// Аккаунт (#128 — из меню под именем, не из навигации): пароль, токены расширения, Telegram, отчёт.
// ?section=password|tokens|telegram|report — прокрутить к блоку (пункты меню аккаунта)
import { api, del, post } from "../api/http";
import type { ApiToken, Telegram, TelegramLink } from "../api/types";
import { date } from "../format";
import { act, app, me } from "../state";
import { Button, Card, Confirm, el, Field, Form, FormGrid, PageHead, safeUrl, Secret, StackTable, Toolbar, type Child } from "@hh-autoapplier/ui";

export async function account(): Promise<Child> {
  const user = me();
  const [tokens, telegram] = await Promise.all([api<ApiToken[]>("/api/tokens"), api<Telegram>("/api/telegram")]);

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

  passwordForm.id = "password";

  // Персональные токены — для расширения
  const tokenName = el("input", { placeholder: "Название, например «ноутбук»", "aria-label": "Название токена", required: true });
  const issue = Button("Выпустить", { kind: "primary", type: "submit" });
  const issued = el("div");
  const tokenForm = Form({ className: "bar", submit: issue, onSubmit: async () => {
    const token = await act(() => post<ApiToken>("/api/tokens", { name: tokenName.value.trim() }), "Токен выпущен");
    if (!token) return;
    tokenName.value = "";
    const fresh = tokenRows(await api<ApiToken[]>("/api/tokens"));
    tokensTable.replaceWith(fresh);
    tokensTable = fresh;
    issued.replaceChildren(el("p", { textContent: "Скопируйте токен в панель расширения — он показывается один раз:" }), el("div", { className: "secret", textContent: token.value ?? "" }));
  } }, tokenName, issue);
  let tokensTable: HTMLElement = tokenRows(tokens);

  function tokenRows(items: ApiToken[]): HTMLElement {
    if (!items.length) return el("p", { className: "muted", textContent: "Токенов нет." });
    return StackTable([{ title: "Название" }, { title: "Начало" }, { title: "Выпущен" }, { title: "" }],
      items.map((t) => {
        const revoke = Button("Отозвать", { kind: "danger", onClick: async () => {
          if (await Confirm({ title: `Отозвать токен «${t.name}»?`, text: "Расширение с ним перестанет работать. Вернуть нельзя — только выпустить новый.", confirm: "Отозвать", kind: "danger" }))
            return act(() => del(`/api/tokens/${t.id}`), () => { app.render(); return `Токен «${t.name}» отозван`; });
        } });
        return [t.name, el("span", { className: "mono", textContent: `${t.prefix}…` }), date(t.createdAt), t.revokedAt ? `отозван ${date(t.revokedAt)}` : revoke];
      }));
  }

  // Telegram
  function telegramView(t: Telegram): HTMLElement {
    if (!t.configured) return el("p", { className: "muted", textContent: "Бот не настроен на сервере (TELEGRAM_BOT_TOKEN)." });
    if (t.linked) {
      const unlink = Button("Отвязать", { kind: "danger", onClick: async () => {
        if (await Confirm({ title: "Отвязать Telegram?", text: "Уведомления перестанут приходить. Привязать заново — по новой ссылке.", confirm: "Отвязать", kind: "danger" }))
          return act(() => del("/api/telegram"), () => { app.render(); return "Telegram отвязан"; });
      } });
      return Toolbar([`Привязан${t.username ? ` к @${t.username}` : ""} ${date(t.linkedAt)}`, unlink]);
    }
    const out = el("div");
    const link = Button("Получить ссылку привязки", { kind: "primary", onClick: async () => {
      const code = await act(() => post<TelegramLink>("/api/telegram/link"), "Код действует 15 минут");
      if (!code) return;
      out.replaceChildren(code.link
        ? el("p", {}, "Откройте ", el("a", { href: safeUrl(code.link), target: "_blank", rel: "noopener", textContent: "ссылку на бота" }), " и нажмите «Start».")
        : el("p", {}, "Отправьте боту: ", Secret(`/start ${code.code}`)));
    } });
    return el("div", {}, Toolbar(["Не привязан", link]), out);
  }

  return [
    PageHead("Аккаунт", `${user.name}, роль ${user.role}`),
    user.viaApiKey ? el("p", { className: "card", textContent: "Вход по токену: пароль, токены и Telegram меняются только после входа по паролю." }) : passwordForm,
    Card({ title: "Токены расширения", id: "tokens" }, tokenForm, issued, tokensTable),
    Card({ title: "Telegram", id: "telegram" }, el("div", {}, telegramView(telegram))),
    Card({ title: "Отчёт", id: "report" }, el("a", { href: "/api/report?format=csv", textContent: "Скачать отчёт по откликам (CSV)" })),
  ];
}
