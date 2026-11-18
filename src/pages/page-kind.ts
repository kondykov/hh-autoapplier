import type { PageType } from "../dom/page-type";
import type { PageKind } from "../shared/panel/model";

// Тип страницы hh.ru → раздел общей панели (#123) и подпись в шапке
export function pageKindOf(page: PageType): { kind: PageKind; title: string } {
  switch (page) {
    case "search":
      return { kind: "search", title: "Выдача" };
    case "vacancy":
      return { kind: "vacancy", title: "Вакансия" };
    case "response":
      return { kind: "vacancy", title: "Отклик" };
    case "chat":
      return { kind: "chat", title: "Чаты" };
    case "unknown":
      return { kind: "other", title: "hh.ru" };
  }
}
