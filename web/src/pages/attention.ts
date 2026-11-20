// «Требует внимания» (#128): что ждёт решения — на дашборде; раньше — «Что дальше» в «Обзоре»
import type { Stats, Telegram } from "../api/types";
import { path } from "../router";
import { Card, el, EmptyState, type Child } from "@hh-autoapplier/ui";

export interface AttentionItem {
  text: string;
  href: string;
  link: string;
}

// Пункты по счётчикам статусов: пустые пропускаются; «Ответы работодателей» — всегда; Telegram — если бот настроен,
// а аккаунт не привязан (при просмотре чужих данных не показывается: привязать можно только себе)
export function attentionItems(stats: Stats, telegram: Telegram | null, readOnly: boolean, dialogs = 0): AttentionItem[] {
  const items: (AttentionItem | null)[] = [
    stats.Review ? { text: `На проверке ${stats.Review}`, href: path("review"), link: "решить" } : null,
    stats.Failed ? { text: `С ошибкой ${stats.Failed}`, href: path("vacancies", { status: "Failed" }), link: "вернуть в очередь или оставить" } : null,
    stats.Skipped ? { text: `Пропущено ${stats.Skipped} (LLM, правила или вы)`, href: path("vacancies", { status: "Skipped" }), link: "пересмотреть и вернуть нужные в очередь" } : null,
    stats.Queued ? { text: `В очереди ${stats.Queued}`, href: path("vacancies", { status: "Queued" }), link: "лишние можно пропустить" } : null,
    { text: dialogs ? `Ответы работодателей: необработанных ${dialogs}` : "Ответы работодателей", href: path("dialogs"), link: "приглашения и вопросы с черновиками" },
    telegram?.configured && !telegram.linked && !readOnly ? { text: "Telegram не привязан", href: path("account", { section: "telegram" }), link: "привязать" } : null,
  ];
  return items.filter((i): i is AttentionItem => i !== null);
}

export const AttentionList = (items: AttentionItem[], extra: Child[] = []) => items.length || extra.length
  ? el("ul", { className: "attention" }, items.map((i) => el("li", {}, `${i.text}: `, el("a", { href: i.href, textContent: i.link }))), extra)
  : EmptyState("Ничего не ждёт решения.", { icon: "check" });

export const AttentionCard = (items: AttentionItem[]) => Card({ title: "Требует внимания" }, AttentionList(items));
