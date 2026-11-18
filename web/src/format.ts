// Подписи и форматы, общие для разделов
import { el, Pill } from "./ui/ui";
import type { Vacancy } from "./api/types";

export const STATUSES: Record<string, string> = {
  New: "новая", Pending: "ждёт оценки", Review: "на проверке", Questionnaire: "анкета", Queued: "в очереди",
  Applied: "отклик отправлен", Invited: "приглашение", Rejected: "отказ", Skipped: "пропущена", Failed: "ошибка",
};

export const date = (value: string | number | null | undefined): string =>
  (value ? new Date(value).toLocaleString("ru-RU", { dateStyle: "short", timeStyle: "short" }) : "");

// Источник вакансии (задел под другие агрегаторы, #32): пока все вакансии — с hh.ru
const SOURCES: Record<string, { name: string; color: string }> = { hh: { name: "hh.ru", color: "var(--src-hh)" } };
export const sourcePill = (v: Pick<Vacancy, "source">) => {
  const s = SOURCES[v.source ?? "hh"] ?? { name: v.source ?? "", color: "var(--src-other)" };
  return Pill(s.name, { dot: s.color, title: "Источник вакансии" });
};

export function salary(v: Pick<Vacancy, "salaryFrom" | "salaryTo" | "salaryCurrency">): string | null {
  if (v.salaryFrom == null && v.salaryTo == null) return null;
  const range = [v.salaryFrom != null ? `от ${v.salaryFrom.toLocaleString("ru-RU")}` : "", v.salaryTo != null ? `до ${v.salaryTo.toLocaleString("ru-RU")}` : ""];
  return `${range.filter(Boolean).join(" ")} ${v.salaryCurrency ?? ""}`.trim();
}

// Список вариантов для <select>: [[value, text]]
export const options = (pairs: [string, string][]) => pairs.map(([value, textContent]) => el("option", { value, textContent }));

// Слова через запятую или с новой строки
export const lines = (text: string) => text.split(/[\n,]/).map((s) => s.trim()).filter(Boolean);
