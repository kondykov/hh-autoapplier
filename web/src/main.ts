// Единый веб-интерфейс (#46, на TS — #130): разделы по пути адреса (History API) поверх того же API, что у расширения.
// Вход — пароль, дальше cookie. Разметка — компонентами UI kit (ui/ui.ts, #129); данные с hh.ru и от LLM — только текстом
import "./styles.css";
import iconsUrl from "./ui/icons.svg";
import { api, post, Unauthorized } from "./api/http";
import type { Dialog, Me, Stats, User } from "./api/types";
import { account } from "./pages/account";
import { admin } from "./pages/admin";
import { dashboard } from "./pages/dashboard";
import { dialogs } from "./pages/dialogs";
import { kit } from "./pages/kit";
import { overview } from "./pages/overview";
import { resumes } from "./pages/resumes";
import { review } from "./pages/review";
import { searches } from "./pages/searches";
import { vacancies } from "./pages/vacancies";
import { isAppLink, legacyHashPath, parseRoute, path, type Route, type RouteName } from "./router";
import { app, page, scoped, session, setStatus } from "./state";
import { initThemeButton } from "./theme";
import { el, Icon, nodes, setIconSprite, Sheet, SheetLinks, TabBar, Tabs, type Child, type IconName, type NavItem } from "./ui/ui";

setIconSprite(iconsUrl);

const byId = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const view = byId<HTMLElement>("view");
const status = byId<HTMLElement>("status");
const loginForm = byId<HTMLFormElement>("login");
const nav = byId<HTMLElement>("nav");
const tabbar = byId<HTMLElement>("tabbar");
const viewAs = byId<HTMLSelectElement>("view-as");

const routes: Record<RouteName, (route: Route) => Promise<Child>> = { dashboard, overview, vacancies, review, dialogs, searches, resumes, account, admin, kit };

// Разделы (#120): tab — место в нижней панели телефона (остальные — в «Ещё»), icon — значок там же.
// Витрина /kit (#129) в навигации не показывается: только по адресу, только администратору
interface Section {
  route: RouteName;
  title: string;
  icon?: IconName;
  tab?: boolean;
  count?: keyof typeof counts;
  admin?: boolean;
}
const SECTIONS: Section[] = [
  { route: "dashboard", title: "Дашборд", icon: "dashboard", tab: true },
  { route: "review", title: "Проверка", icon: "review", tab: true, count: "review" },
  { route: "dialogs", title: "Ответы", icon: "dialogs", tab: true, count: "dialogs" },
  { route: "vacancies", title: "Вакансии", icon: "vacancies", tab: true },
  { route: "overview", title: "Обзор" },
  { route: "searches", title: "Поиски" },
  { route: "resumes", title: "Резюме" },
  { route: "account", title: "Профиль" },
  { route: "admin", title: "Администрирование", admin: true },
];
const counts = { review: 0, dialogs: 0 };
const more = Sheet([], { label: "Ещё разделы" });
more.id = "more";
document.body.append(more);

function buildNav(route: RouteName) {
  const visible = SECTIONS.filter((s) => !s.admin || session.me?.role === "Admin");
  const item = (s: Section): NavItem => ({ href: path(s.route), label: s.title, icon: s.icon, active: s.route === route, count: s.count ? counts[s.count] : 0 });
  nav.replaceChildren(Tabs(visible.map(item)));
  const rest = visible.filter((s) => !s.tab);
  const moreButton = el("button", { type: "button", className: rest.some((s) => s.route === route) ? "active" : "", "aria-label": "Ещё разделы" }, Icon("more"), "Ещё");
  moreButton.onclick = (e) => { e.stopPropagation(); more.toggle(); };
  tabbar.replaceChildren(...TabBar(visible.filter((s) => s.tab).map(item), moreButton));
  more.replaceChildren(...SheetLinks(rest.map(item)));
  more.close();
}

// Счётчики в навигации: на проверке и необработанные ответы (кроме отказов) — как в «Ответах работодателей»
async function loadCounts() {
  const [stats, open] = await Promise.all([
    api<Stats>(scoped("/api/stats")).catch(() => ({} as Stats)),
    api<Dialog[]>(scoped("/api/dialogs?take=200&handled=false")).catch(() => [] as Dialog[]),
  ]);
  counts.review = stats.Review ?? 0;
  counts.dialogs = open.filter((m) => m.kind !== "Rejection").length;
}

async function render() {
  if (!session.me) return;
  const current = ++page.renders;
  const route = parseRoute(location.pathname, location.search);
  buildNav(route.name);
  try {
    const content = await routes[route.name](route);
    if (current !== page.renders) return;
    view.replaceChildren(...nodes(content));
  } catch (e) {
    if (current !== page.renders) return;
    view.replaceChildren();
    if (e instanceof Unauthorized) showLogin();
    setStatus((e as Error).message, true);
  }
}

// Переход внутри веба: адрес меняется без перезагрузки; replace — без новой записи в истории, render: false — только адрес
function navigate(url: string, { replace = false, render: draw = true }: { replace?: boolean; render?: boolean } = {}) {
  if (url !== location.pathname + location.search) {
    if (replace) history.replaceState(null, "", url);
    else history.pushState(null, "", url);
  }
  if (!draw) return;
  setStatus(session.viewedUser ? status.textContent ?? "" : "");
  window.scrollTo(0, 0);
  render();
}

async function loadUsers() {
  if (session.me?.role !== "Admin") return;
  const me = session.me;
  const users = await api<User[]>("/api/users").catch(() => [] as User[]);
  viewAs.replaceChildren(el("option", { value: "", textContent: "мои" }),
    ...users.filter((u) => u.id !== me.id).map((u) => el("option", { value: u.id, textContent: u.email })));
  viewAs.value = session.viewedUser ?? "";
  byId("viewing").hidden = users.length < 2;
}

function showLogin() {
  session.me = null;
  view.replaceChildren();
  nav.hidden = tabbar.hidden = more.hidden = true;
  byId("logout").hidden = true;
  byId("viewing").hidden = true;
  byId("who").textContent = "";
  loginForm.hidden = false;
  byId("email").focus();
}

async function showSession(user: Me) {
  session.me = user;
  loginForm.hidden = true;
  nav.hidden = tabbar.hidden = false;
  byId("logout").hidden = false;
  byId("who").textContent = user.name;
  await Promise.all([loadUsers(), loadCounts()]);
  await render();
}

Object.assign(app, { render, showLogin, navigate, loadUsers });
initThemeButton(byId<HTMLButtonElement>("theme"));

loginForm.onsubmit = async (e) => {
  e.preventDefault();
  const password = byId<HTMLInputElement>("password");
  try {
    await post("/api/auth/login", { email: byId<HTMLInputElement>("email").value.trim(), password: password.value });
    password.value = "";
    setStatus("");
    // Вход отвечает учётной записью без признака входа по токену — берём то же, что при открытии страницы
    await showSession(await api<Me>("/api/auth/me"));
  } catch (err) {
    setStatus((err as Error).message, true);
  }
};

byId("logout").onclick = async () => {
  await post("/api/auth/logout").catch(() => null);
  setStatus("");
  showLogin();
};

viewAs.onchange = () => {
  session.viewedUser = viewAs.value || null;
  setStatus(session.viewedUser ? `Данные ${viewAs.selectedOptions[0].textContent} — только просмотр` : "");
  loadCounts().then(render);
};

// Ссылки на разделы — без перезагрузки страницы; новая вкладка, файлы и /api — как обычно
document.addEventListener("click", (e) => {
  const a = (e.target as Element | null)?.closest?.("a");
  if (!a || e.defaultPrevented) return;
  if (!isAppLink({ href: a.href, target: a.target, download: a.hasAttribute("download"), button: e.button,
    modified: e.ctrlKey || e.metaKey || e.shiftKey || e.altKey }, location.origin)) return;
  e.preventDefault();
  const url = new URL(a.href);
  navigate(url.pathname + url.search);
});

window.addEventListener("popstate", () => { setStatus(session.viewedUser ? status.textContent ?? "" : ""); render(); });

// Старые ссылки и закладки на якорях (/#review, /#vacancies/Queued) — на пути
const legacy = legacyHashPath(location.hash);
if (legacy) history.replaceState(null, "", legacy);
window.addEventListener("hashchange", () => {
  const target = legacyHashPath(location.hash);
  if (target) navigate(target, { replace: true });
});

// Сессия могла остаться с прошлого раза
api<Me>("/api/auth/me").then(showSession).catch(() => showLogin());
