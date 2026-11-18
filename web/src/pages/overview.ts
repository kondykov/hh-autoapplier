// Обзор: счётчики по статусам и что делать дальше
import { api } from "../api/http";
import type { Stats as StatusStats, Telegram, VacancyStatus } from "../api/types";
import { STATUSES } from "../format";
import { path } from "../router";
import { readOnly, scoped } from "../state";
import { Card, el, EmptyState, PageHead, Stat, Stats, type Child } from "../ui/ui";

export async function overview(): Promise<Child> {
  const [stats, telegram] = await Promise.all([
    api<StatusStats>(scoped("/api/stats")),
    readOnly() ? null : api<Telegram>("/api/telegram").catch(() => null),
  ]);
  const order: VacancyStatus[] = ["Queued", "Review", "Questionnaire", "Pending", "Applied", "Invited", "Rejected", "Skipped", "Failed", "New"];
  const total = Object.values(stats).reduce((a, b) => a + (b ?? 0), 0);
  const tiles = order.filter((s) => stats[s]).map((s) => Stat({ value: stats[s] ?? 0, label: STATUSES[s], href: path("vacancies", { status: s }) }));

  return [
    PageHead("Обзор", `Всего откликов в работе: ${total}. Нажмите на счётчик — откроется список.`),
    tiles.length ? Stats(tiles, { tiles: true }) : EmptyState("Откликов пока нет: добавьте поиск — сервер соберёт вакансии."),
    Card({ title: "Что дальше" },
      el("ul", {},
        stats.Review ? el("li", {}, `На проверке ${stats.Review}: `, el("a", { href: path("review"), textContent: "решить" })) : null,
        stats.Failed ? el("li", {}, `С ошибкой ${stats.Failed}: `, el("a", { href: path("vacancies", { status: "Failed" }), textContent: "вернуть в очередь или оставить" })) : null,
        stats.Skipped ? el("li", {}, `Пропущено ${stats.Skipped} (LLM, правила или вы): `, el("a", { href: path("vacancies", { status: "Skipped" }), textContent: "пересмотреть и вернуть нужные в очередь" })) : null,
        stats.Queued ? el("li", {}, `В очереди ${stats.Queued}: `, el("a", { href: path("vacancies", { status: "Queued" }), textContent: "лишние можно пропустить" })) : null,
        el("li", {}, "Ответы работодателей — ", el("a", { href: path("dialogs"), textContent: "приглашения и вопросы с черновиками" })),
        el("li", {}, "Отчёт по откликам — ", el("a", { href: scoped("/api/report?format=csv"), textContent: "CSV" })),
        telegram?.configured && !readOnly() ? el("li", {}, telegram.linked ? `Telegram привязан${telegram.username ? ` (@${telegram.username})` : ""}` : "Telegram не привязан — ",
          telegram.linked ? null : el("a", { href: path("account"), textContent: "привязать в профиле" })) : null)),
  ];
}
