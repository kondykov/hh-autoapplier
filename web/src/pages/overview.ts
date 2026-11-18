// Обзор: счётчики по статусам и что делать дальше. С #128 — не в навигации, только по адресу /overview
// (до решения владельца); «Что дальше» — те же пункты, что «Требует внимания» на дашборде
import { api } from "../api/http";
import type { Stats as StatusStats, Telegram, VacancyStatus } from "../api/types";
import { STATUSES } from "../format";
import { path } from "../router";
import { readOnly, scoped } from "../state";
import { Card, el, EmptyState, PageHead, Stat, Stats, type Child } from "../ui/ui";
import { attentionItems, AttentionList } from "./attention";

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
    Card({ title: "Что дальше" }, AttentionList(attentionItems(stats, telegram, readOnly()),
      [el("li", {}, "Отчёт по откликам — ", el("a", { href: scoped("/api/report?format=csv"), textContent: "CSV" }))])),
  ];
}
