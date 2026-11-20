// Ответы работодателей (#26): разбор LLM и черновик; отвечает человек — в чате hh.ru. Фильтр — в адресе: /dialogs?filter=all
import { api, post } from "../api/http";
import type { Dialog } from "../api/types";
import { date, options } from "../format";
import { path, type Route } from "../router";
import { act, app, readOnly, scoped } from "../state";
import { Badge, Button, Callout, Card, el, EmptyState, Field, Meta, PageHead, Quote, Result, Toolbar, type Child, type Tone } from "../ui/ui";

const KINDS: Record<string, string> = { Invitation: "Приглашение", Question: "Вопрос", Rejection: "Отказ", Other: "Прочее" };
const KIND_TONES: Record<string, Tone> = { Invitation: "ok", Question: "accent" };
const AUTO_REPLY: Record<string, string> = { Pending: "автоответ ждёт отправки", Sent: "автоответ отправлен", Unavailable: "автоответ невозможен" };

export async function dialogs(route: Route): Promise<Child> {
  const open = route.query.get("filter") !== "all";
  const items = (await api<Dialog[]>(scoped(`/api/dialogs?take=200${open ? "&handled=false" : ""}`))).filter((m) => !open || m.kind !== "Rejection");
  const select = el("select", { "aria-label": "Какие сообщения показать" }, options([["open", "Необработанные, кроме отказов"], ["all", "Все"]]));
  select.value = open ? "open" : "all";
  select.onchange = () => app.navigate(path("dialogs", { filter: select.value === "all" ? "all" : null }));

  return [
    PageHead("Ответы работодателей", "Сообщения из чатов hh.ru присылает расширение («Автоответ» на странице чатов). LLM разбирает приглашения и вопросы и предлагает ответ — отправляете вы. Отказы распознаются без LLM."),
    Toolbar([select, el("span", { className: "muted", textContent: `Сообщений: ${items.length}` })]),
    items.length ? items.map(dialogCard)
      : EmptyState(open ? "Новых ответов работодателей нет." : "Сообщений пока нет: нажмите «Автоответ» в панели расширения на странице чатов hh.ru."),
  ];
}

function dialogCard(m: Dialog): HTMLElement {
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
  Meta([m.company, date(m.at), m.hhState && `hh: ${m.hhState}`, m.autoReply && AUTO_REPLY[m.autoReply], m.autoReplyError, m.handledAt && "обработано"],
    { lead: Badge((m.kind && KINDS[m.kind]) || "ждёт разбора", { tone: m.kind ? KIND_TONES[m.kind] : undefined }) }),
  m.summary ? Callout(m.summary) : null,
  Quote(m.text),
  rejection ? null : Field(m.llmModel ? `Черновик ответа (${m.llmModel}) — поправьте [заглушки] перед отправкой` : "Черновик ответа", reply, { stacked: true }));
}
