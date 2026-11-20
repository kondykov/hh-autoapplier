// LLM → Подключения (#127, только администратор): подключения сервера без учётных данных (ключ — маской), маршруты по умолчанию,
// состояние связи (#132), «Проверить» и ручной запуск задач LLM (перенесён из «Системы»). Правка ключей и маршрутов — в .env, не здесь
import { api, post } from "../api/http";
import type { LlmConnection, LlmServerConnections, Links } from "../api/types";
import { date } from "../format";
import { path } from "../router";
import { act, setStatus } from "../state";
import { Badge, Button, Card, el, EmptyState, Meter, Pill, StackTable, Toolbar, type Child } from "@hh-autoapplier/ui";
import { connectionState, LLM_TASKS } from "./llm";

const KINDS: Record<string, string> = { main: "основной", route: "маршрут задачи", named: "для выбора" };
const taskTitle = (task: string) => LLM_TASKS.find(([t]) => t === task)?.[1] ?? task;

type JobResult = Record<string, string | number>;

// Что проверять: подключение для выбора — по имени, маршрут задачи — по задаче, основной — без параметров
export function checkPath(kind: string, name: string): string {
  if (kind === "named") return `/api/llm/check?connection=${encodeURIComponent(name)}`;
  if (kind === "route") return `/api/llm/check?task=${encodeURIComponent(name)}`;
  return "/api/llm/check";
}

const usage = (c: LlmConnection): Node | string => c.dailyLimit
  ? el("div", {}, `${c.usedToday} / ${c.dailyLimit}`, Meter(c.usedToday, c.dailyLimit, { label: `Расход ${c.name}`,
    color: c.usedToday >= c.dailyLimit ? "var(--bad)" : c.usedToday >= c.dailyLimit * 0.8 ? "var(--warn)" : undefined }))
  : `${c.usedToday} · без лимита`;

const state = (c: LlmConnection): HTMLElement => el("span", {},
  Pill(connectionState(c), { dot: c.available ? "var(--ok)" : "var(--warn)" }),
  c.pauseReason ? el("div", { className: "muted small", textContent: c.pauseReason }) : null);

export async function connections(): Promise<Child> {
  const [data, links] = await Promise.all([api<LlmServerConnections>("/api/llm/connections"), api<Links>("/api/health/links").catch(() => null)]);

  const list = Card({ title: "Подключения сервера" },
    data.connections.length
      ? StackTable([{ title: "Подключение" }, { title: "Провайдер и модель" }, { title: "Адрес" }, { title: "Ключ" }, { title: "Сегодня (UTC)" }, { title: "Состояние" }, { title: "" }],
        data.connections.map(({ kind, connection: c, host, key }) => [
          el("span", {}, el("b", { textContent: c.name }), " ", Badge(KINDS[kind] ?? kind)),
          el("span", {}, c.provider, " · ", el("span", { textContent: c.model })),
          host ? el("span", { className: "url", textContent: host }) : "—",
          // Маска ключа («задан» или «••••7f2c») — метка одной строкой, не Secret: тот для длинных токенов и рвёт текст где угодно
          key ? Pill(key) : el("span", { className: "muted", textContent: "не задан" }),
          usage(c),
          state(c),
          Button("Проверить", { size: "sm", onClick: () => { setStatus(`Проверяем ${c.name}…`);
            return act(() => post<JobResult>(checkPath(kind, c.name)), (r) => `${c.name}: ${r.model} ответила за ${r.latencyMs} мс`); } }),
        ]))
      : EmptyState("LLM не настроена: задайте LLM_API_KEY.", { icon: "llm" }),
    el("p", { className: "muted small", textContent: `Прокси сервера: ${data.proxy ?? "нет (напрямую)"}. «Проверить» — один запрос дневного лимита подключения. Ключи, модели и маршруты задаются в .env на сервере (Llm__…); из веба не меняются.` }));

  const routes = Card({ title: "Маршруты по умолчанию" },
    StackTable([{ title: "Задача" }, { title: "Подключение" }, { title: "Состояние" }],
      data.defaults.map((d) => [taskTitle(d.task), el("span", {}, el("b", { textContent: d.connection.name }), " · ", d.connection.model), state(d.connection)])),
    el("p", { className: "muted small" }, "Так задача идёт, если пользователь не выбрал свою модель в ", el("a", { href: path("llm", {}, "models"), textContent: "«Мои модели»" }), "."));

  return [list, el("div", { className: "cols" }, routes, linksCard(links)), jobsCard()];
}

// Связь (#132): направления по последней проверке и открытые сбои
function linksCard(links: Links | null): HTMLElement {
  if (!links) return Card({ title: "Связь" }, EmptyState("Состояние связи недоступно."));
  if (!links.enabled) return Card({ title: "Связь" }, EmptyState("Проверка связи выключена на сервере."));
  return Card({ title: "Связь", meta: [links.proxy ? `прокси ${links.proxy}` : "без прокси", links.checkedAt ? `проверено ${date(links.checkedAt)}` : null] },
    el("div", { className: "chips" }, links.links.map((l) => Pill(l.link, { dot: l.state === "Ok" ? "var(--ok)" : "var(--bad)",
      title: l.state === "Ok" ? "в порядке" : `${l.state}${l.detail ? `: ${l.detail}` : ""}` }))),
    links.open.length
      ? el("ul", { className: "reasons" }, links.open.map((o) => el("li", {},
        el("span", {}, Badge("сбой", { tone: "bad" }), `${o.subject}: ${o.reason}${o.detail ? ` — ${o.detail}` : ""}`),
        el("span", { className: "muted small", textContent: `с ${date(o.startedAt)}` }))))
      : el("p", { className: "muted", textContent: "Открытых сбоев нет." }));
}

// Ручной запуск фоновых проходов (перенесён из «Системы»): обычно они идут сами, раз в минуту при наличии работы
function jobsCard(): HTMLElement {
  const jobs: [string, string, object, (r: JobResult) => string][] = [
    ["Оценить ожидающие", "/api/llm/evaluate", { limit: 20 }, (r) => `Оценено ${r.evaluated}: подходят ${r.fit}, сомнительных ${r.doubtful}, отклонено ${r.rejected}; осталось ${r.remaining}`],
    ["Ответить на анкеты", "/api/llm/answers", { limit: 20 }, (r) => `Анкеты: отвечено ${r.answered}, не вышло ${r.failed}; осталось ${r.remaining}`],
    ["Написать письма", "/api/llm/letters", { limit: 20 }, (r) => `Письма: написано ${r.written}, не вышло ${r.failed}; осталось ${r.remaining}`],
  ];
  return Card({ title: "Запустить сейчас" },
    el("p", { className: "muted", textContent: "Обычно не нужно: LLM работает раз в минуту, если есть работа. До 20 вакансий за раз, тратит лимит LLM." }),
    Toolbar(jobs.map(([text, url, body, done]) => Button(text, { onClick: () => { setStatus(`${text}…`); return act(() => post<JobResult>(url, body), done); } })),
      { flush: true }));
}
