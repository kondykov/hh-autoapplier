import { SELECTORS } from "../core/selectors";

// Признак архива со страницы вакансии (#70). Форма отклика архивной вакансии открывается как обычно — с резюме,
// письмом и кнопкой отправки, признака там нет (проверено 2026-11-10). Страница /vacancy/{id} отдаётся с кодом 200
// и со входом, и без; признак — status.archived в JSON HH-Lux-InitialState, closedForApplicants у архивных false
// unavailable — вакансию закрыл hh.ru: страница отдаёт 403, а главная открывается (#99)
// responses — число откликов на вакансию с той же страницы (#148); нет в данных — поля нет
export type ArchiveCheck = { state: "archived" } | { state: "open"; responses?: number } | { state: "unavailable" } | { state: "unknown"; error: string };

type PageState = { vacancyView?: { vacancyFull?: { vacancy?: unknown; extraVacancyFields?: Record<string, unknown> } } };

// Число откликов (#148): текстом соискателю hh.ru его не показывает, только в данных страницы — точным числом
// (проверено 2026-11-20 без входа): vacancyFull.extraVacancyFields.vacancyResponsesCount и totalResponsesCount.
// Поля расходятся на единицы–десятки (разные кэши hh.ru) — берётся большее, как и при сборе на сервере (HhPageParser)
export function parseResponses(state: PageState | null | undefined): number | undefined {
  const extra = state?.vacancyView?.vacancyFull?.extraVacancyFields;
  const counts = [extra?.vacancyResponsesCount, extra?.totalResponsesCount]
    .filter((n): n is number => typeof n === "number" && Number.isInteger(n) && n >= 0);
  return counts.length ? Math.max(...counts) : undefined;
}

export interface ArchiveChecker {
  check(id: string): Promise<ArchiveCheck>;
}

// Архив — только явное status.archived === true у той же вакансии; всё остальное — не архив или «не знаю»
export function parseArchiveState(html: string, id: string): ArchiveCheck {
  const doc = new DOMParser().parseFromString(html, "text/html");
  const el = doc.querySelector<HTMLTemplateElement>(SELECTORS.pageInitialState);
  // В браузере текст шаблона лежит в content, а не в самом элементе
  const json = el?.content?.textContent || el?.textContent;
  if (!json) return { state: "unknown", error: "на странице нет данных" };

  let vacancy: { vacancyId?: unknown; status?: { archived?: unknown } } | undefined;
  let state: PageState | undefined;
  try {
    state = JSON.parse(json) as PageState;
    vacancy = state?.vacancyView?.vacancyFull?.vacancy as typeof vacancy;
  } catch {
    return { state: "unknown", error: "данные страницы не разобраны" };
  }
  if (!vacancy) return { state: "unknown", error: "на странице нет вакансии" };
  if (String(vacancy.vacancyId) !== id) return { state: "unknown", error: `на странице другая вакансия ${String(vacancy.vacancyId)}` };

  const archived = vacancy.status?.archived;
  if (archived === true) return { state: "archived" };
  if (archived === false) {
    const responses = parseResponses(state);
    return responses === undefined ? { state: "open" } : { state: "open", responses };
  }
  return { state: "unknown", error: "нет признака архива" };
}

// Один GET без cookie: страница публичная, и запрос не попадает в историю просмотров владельца
export function pageArchiveChecker(origin: () => string, fetchFn: typeof fetch = (input, init) => fetch(input, init), timeoutMs = 10000): ArchiveChecker {
  return {
    async check(id) {
      const abort = new AbortController();
      const timer = setTimeout(() => abort.abort(), timeoutMs);
      try {
        const res = await fetchFn(new URL(`/vacancy/${encodeURIComponent(id)}`, origin()).toString(), { credentials: "omit", signal: abort.signal });
        // Закрытая вакансия (скрыта, снята модерацией) отдаёт 403 всем, со входом и без (139119374, 2026-11-13).
        // Отказ всем запросам — тоже 403: главная открылась — закрыта именно эта вакансия, «Отправить» дала бы ошибку
        if (res.status === 403) {
          const home = await fetchFn(new URL("/", origin()).toString(), { credentials: "omit", signal: abort.signal });
          return home.ok ? { state: "unavailable" } : { state: "unknown", error: "ответ 403" };
        }
        if (!res.ok) return { state: "unknown", error: `ответ ${res.status}` };
        return parseArchiveState(await res.text(), id);
      } catch (e) {
        return { state: "unknown", error: abort.signal.aborted ? "нет ответа" : (e as Error).message };
      } finally {
        clearTimeout(timer);
      }
    },
  };
}
