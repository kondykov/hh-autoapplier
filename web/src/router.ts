// Роутинг веба на путях (#130, History API): /dashboard, /review, /vacancies?status=Queued, /kit/frame…
// Backend отдаёт index.html на любой путь, кроме /api, /health и файлов (WebInterface.cs), поэтому обновление страницы
// открывает тот же раздел. Старые адреса на якорях (/#review, /#vacancies/Queued) переводятся на пути

export const ROUTES = ["dashboard", "overview", "vacancies", "review", "dialogs", "searches", "candidate", "account", "system", "kit"] as const;
export type RouteName = (typeof ROUTES)[number];

// Переименованные разделы (#128): старый адрес → новый раздел (параметры и подраздел сохраняются)
const RENAMED: Record<string, RouteName> = { resumes: "candidate", admin: "system" };

export interface Route {
  name: RouteName;
  // Подраздел — второй сегмент пути (/kit/frame → "frame")
  sub?: string;
  query: URLSearchParams;
}

const isRoute = (name: string): name is RouteName => (ROUTES as readonly string[]).includes(name);

// Неизвестный путь и «/» — дашборд
export function parseRoute(pathname: string, search = ""): Route {
  const [first = "", sub] = pathname.replace(/^\/+/, "").split("/");
  const name = isRoute(first) ? first : "dashboard";
  return { name, sub: sub ? decodeURIComponent(sub) : undefined, query: new URLSearchParams(search) };
}

// Старый адрес переименованного раздела → новый: /resumes → /candidate, /admin?x=1 → /system?x=1; остальные — null
export function renamedPath(pathname: string, search = ""): string | null {
  const [, first = "", ...rest] = pathname.split("/");
  const target = RENAMED[first];
  return target ? ["", target, ...rest].join("/") + search : null;
}

// Адрес раздела: path("vacancies", { status: "Queued" }) → /vacancies?status=Queued; пустые параметры пропускаются
export function path(name: RouteName, params: Record<string, string | null | undefined> = {}, sub?: string): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value) query.set(key, value);
  const q = query.toString();
  return `/${name}${sub ? `/${encodeURIComponent(sub)}` : ""}${q ? `?${q}` : ""}`;
}

// Старый якорь → путь: #vacancies/Queued → /vacancies?status=Queued, #dialogs/all → /dialogs?filter=all, #kit/frame → /kit/frame.
// Пустой якорь — null (переходить некуда), неизвестный раздел — дашборд
export function legacyHashPath(hash: string): string | null {
  const value = hash.replace(/^#/, "");
  if (!value) return null;
  const [raw, arg] = value.split("/");
  const name = RENAMED[raw] ?? raw;
  if (!isRoute(name)) return path("dashboard");
  if (!arg) return path(name);
  if (name === "vacancies") return path(name, { status: decodeURIComponent(arg) });
  if (name === "dialogs") return path(name, { filter: decodeURIComponent(arg) });
  return path(name, {}, decodeURIComponent(arg));
}

// Пути, которые не страницы веба: API, проверка живости, документация API
const SERVER_PATHS = /^\/(api|health|openapi|scalar)(\/|$)/i;

export interface LinkClick {
  href: string;
  target?: string;
  download?: boolean;
  button: number;
  modified: boolean;
}

// Клик по ссылке — переход внутри веба без перезагрузки: та же вкладка, левая кнопка без Ctrl/Shift/Alt/Cmd,
// тот же источник, не путь сервера и не файл (у пути нет расширения)
export function isAppLink(click: LinkClick, origin: string): boolean {
  if (click.button !== 0 || click.modified || click.download || (click.target && click.target !== "_self")) return false;
  let url: URL;
  try {
    url = new URL(click.href, origin);
  } catch {
    return false;
  }
  if (url.origin !== origin) return false;
  if (SERVER_PATHS.test(url.pathname)) return false;
  return !/\.[a-z0-9]+$/i.test(url.pathname);
}
