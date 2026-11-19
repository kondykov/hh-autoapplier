// Витрина UI kit (#129): все компоненты во всех состояниях на выдуманных данных — по ней проверяются тема и телефон,
// из неё собираются концепты. /kit/frame — та же витрина без переключателей (для просмотра в рамке заданной ширины)
import { options } from "../format";
import { path, type Route } from "../router";
import { me } from "../state";
import { currentTheme, setTheme, type Theme } from "../theme";
import {
  Actions, Badge, Bars, Button, Callout, Caption, Card, Chips, Confirm, Count, Disclosure, el, EmptyState, Field, Form, FormGrid, Funnel, Icon, ICONS, KeyHints, Meta, Meter,
  Menu, Note, PageHead, Pill, Pre, Quote, RankList, Result, Secret, Segmented, Sheet, SheetLinks, Skeleton, StackTable, Stat, Stats, Tabs, Toast,
  FilterList, PickRow, SelectionBar, type PickElement, Alert, CommandPalette, SearchButton,
  type Child, type MenuElement, type PaletteElement, type SelectionBarElement, type SheetElement,
} from "../ui/ui";
import { AGGREGATORS } from "./dashboard";
import { PERIODS } from "./dashboard-data";

const kitState: { themes: "one" | "both"; width: "auto" | 375 | 720 } = { themes: "one", width: "auto" };
const KIT = path("kit");

function kitGallery(): HTMLElement {
  const sec = (title: string, ...children: Child[]) => el("section", { className: "kit-sec" }, el("h2", { textContent: title }), children);
  const row = (label: string, ...children: Child[]) => el("div", { className: "kit-row" }, el("span", { className: "muted", textContent: label }), children);
  const wait = (ms = 1500) => () => new Promise((r) => setTimeout(r, ms));
  const busyButton = Button("Сохраняю…", { kind: "primary" });
  busyButton.disabled = true;
  busyButton.setAttribute("aria-busy", "true");
  const tokens = ["--bg", "--card", "--card2", "--text", "--muted", "--border", "--accent", "--accent-soft", "--ok", "--bad", "--warn", "--warn-bg", "--bar", "--track"];
  const segValue = { v: 30 };
  const chipValue = { v: "hh" };
  const sheet = Sheet(SheetLinks([{ href: KIT, label: "Обзор", icon: "overview", active: true }, { href: KIT, label: "Поиски", icon: "search" }, { href: KIT, label: "Резюме", icon: "resume" }]), { title: "Нижний лист" });
  sheet.classList.add("kit-demo");
  const menu = Menu("Анна", [{ label: "Аккаунт", icon: "user", href: KIT, active: true }, { label: "Отчёт CSV", icon: "download", href: KIT }, "sep",
    { label: "Тема", icon: "theme", keepOpen: true, onClick: () => { Toast("Пункт с keepOpen: меню не закрылось"); } }, { label: "Выйти", icon: "logout", danger: true, onClick: () => { Toast("Выход"); } }],
    { label: "Меню аккаунта", align: "start" });
  menu.classList.add("kit-demo");
  // Menu с триггером-кнопкой (#107): действие с выбором варианта
  const menuButton = Menu("Оценить заново через", [{ label: "gigachat · GigaChat-2-Max", onClick: () => { Toast("Оценка через gigachat"); } },
    { label: "groq-fast · gpt-oss-20b — пауза", onClick: () => { Toast("Оценка через groq-fast"); } }], { label: "Оценить заново через подключение", align: "start", button: true });
  menuButton.classList.add("kit-demo");
  document.body.append(sheet);
  // SelectionBar над строками PickRow: флажок «выбрать все», действия — в строке, на телефоне — в листе
  const pickRows: PickElement[] = [];
  const pickBar = SelectionBar(() => [Button("Вернуть в очередь", { kind: "primary", icon: "undo", onClick: () => { Toast("Вернули в очередь", { undo: () => Toast("Снова в отсеве") }); } }),
    Button("Скрыть", { onClick: () => { Toast("Скрыто"); } })], { onAll: (on) => { pickRows.forEach((r) => { r.checked = on; }); syncPicks(); } });
  pickBar.classList.add("kit-demo");
  const syncPicks = () => pickBar.update(pickRows.filter((r) => r.checked).length, pickRows.length);
  pickRows.push(
    PickRow({ title: "Backend-разработчик C#/Go", meta: ["Облако-9", "2 ч назад"], note: "LLM: основной стек Go, C# — вторичный", score: 64, tone: "accent", checked: true, onToggle: syncPicks,
      actions: Button("Вернуть", { size: "sm", icon: "undo", iconOnly: true }) }),
    PickRow({ title: "PHP-разработчик (Symfony)", meta: "Диджитал Плюс", note: "LLM: стек PHP, в профиле — C#/.NET", score: 22, onToggle: syncPicks }));
  const picks = el("div", {}, pickRows);
  syncPicks();
  const sheetButton = Button("Открыть лист", { icon: "more", onClick: (e) => { e.stopPropagation(); sheet.toggle(); } });
  // Палитра на выдуманных пунктах; «Вакансии» — источник с запросом, как за флагом features.vacancySearch
  const demoVacancies = ["C# разработчик (выдуманная)", "Backend-разработчик C#/Go", "PHP-разработчик (Symfony)", "Разработчик .NET (поддержка)"];
  const palette = CommandPalette({
    hotkey: false,
    groups: () => [
      { title: "Разделы", items: [{ label: "Дашборд", icon: "dashboard", href: KIT }, { label: "LLM · Отсев", icon: "llm", href: KIT }, { label: "Система", icon: "admin", href: KIT }] },
      { title: "Команды", items: [{ label: "Тема: тёмная", icon: "moon", hint: "сменить", onRun: () => { Toast("Тема"); } },
        { label: "Запустить сбор вакансий", icon: "download", hint: "администратор", onRun: () => { Toast("Сбор запущен"); } }] },
    ],
    sources: [{ title: "Вакансии", search: async (q) => demoVacancies.filter((v) => v.toLowerCase().includes(q.toLowerCase()))
      .map((v) => ({ label: v, hint: "ООО «Пример»", icon: "external" as const, href: "https://example.com/vacancy/1", external: true })) }],
  });
  palette.classList.add("kit-demo");
  const compactSearch = SearchButton("Найти", { onClick: () => palette.open() });
  compactSearch.classList.add("compact");

  return el("div", {},
    sec("Токены",
      el("div", { className: "kit-swatches" }, tokens.map((t) => el("div", {}, el("i", { style: `background:var(${t})` }), t))),
      el("p", { className: "muted small", textContent: "Шрифты: --sans (текст), --mono (числа, метки, логотип). Радиусы --r-xs…--r-xl, отступы --sp-1…--sp-6 (шаг 4px), касание --tap 44px." })),
    sec("Иконки", el("div", { className: "kit-icons" }, ICONS.map((name) => el("div", {}, Icon(name), name)))),
    sec("Button",
      row("виды", Button("Обычная"), Button("Primary", { kind: "primary" }), Button("Ok", { kind: "ok" }), Button("Danger", { kind: "danger" }), Button("Ghost", { kind: "ghost" })),
      row("размеры", Button("sm", { size: "sm" }), Button("обычная"), Button("lg", { size: "lg", kind: "primary" })),
      row("с иконкой", Button("Скопировать", { icon: "copy" }), Button("Вернуть", { icon: "undo", kind: "ghost" }), Button("Тема", { icon: "theme", iconOnly: true }), Button("Ещё", { icon: "more", iconOnly: true })),
      row("состояния", Button("Выключена", { disabled: true }), Button("Primary выкл.", { kind: "primary", disabled: true }), busyButton,
        Button("Нажмите — занята 1,5 с", { kind: "primary", onClick: wait() }))),
    sec("Pill, Badge, Count",
      row("Pill", Pill("в очереди"), Pill("приглашение", { tone: "ok" }), Pill("ошибка", { tone: "bad" }), Pill("на проверке", { tone: "accent" }), Pill("hh.ru", { dot: "var(--src-hh)" }), Pill("hirify", { dot: "var(--src-hirify)" })),
      row("Badge", Badge("Приглашение", { tone: "ok" }), Badge("Вопрос", { tone: "accent" }), Badge("Отказ", { tone: "bad" }), Badge("Прочее")),
      row("Count", el("span", {}, "Проверка", Count(7)), el("span", {}, "Ответы", Count(120)))),
    sec("Card",
      el("div", { className: "cols" },
        Card({ title: "C# разработчик (выдуманная)", href: "https://example.com/vacancy/1", meta: ["ООО «Пример»", "Москва", "от 250 000 RUR", "удалённо", "балл 82"],
          actions: [Button("В очередь", { kind: "primary" }), Button("Скрыть"), Result()] },
        Callout("LLM: стек совпадает, но в тексте — 5 лет опыта, у кандидата 3."), Disclosure("Описание вакансии", Pre("Описание вакансии с переносами строк.\nВторая строка."))),
        Card({ title: "Решённая карточка", meta: "одобрено, в очереди", decided: true, actions: [Result("одобрено, в очереди")] }, Quote("Здравствуйте! Приглашаем на собеседование в четверг."))),
      Card({ tight: true }, "Плотная карточка (tight) — строка навыка в профиле.")),
    sec("Tabs, Segmented, Chips",
      Tabs([{ href: KIT, label: "Дашборд", active: true }, { href: KIT, label: "Проверка", count: 7 }, { href: KIT, label: "Ответы", count: 4 }, { href: KIT, label: "Вакансии" }], { label: "Пример вкладок" }),
      el("div", { style: "height:12px" }),
      row("Segmented", Segmented(PERIODS.map((p) => ({ value: p as number, label: `${p} дн` })), segValue.v, (v) => { segValue.v = v; }, { label: "Период" })),
      Chips(AGGREGATORS, chipValue.v, (v) => { chipValue.v = v; }, { label: "Агрегатор" })),
    sec("Stat",
      Stats([
        Stat({ value: 128, label: "собрано вакансий", note: "+24% к прошлым 30 дн", trend: "up" }),
        Stat({ value: 31, label: "откликов отправлено", note: "-12% к прошлым 30 дн", trend: "down" }),
        Stat({ value: 9, unit: "%", label: "в приглашение", note: "3 приглашения за период" }),
        Stat({ value: 0, label: "отказов", note: "—" }),
      ]),
      Stats([Stat({ value: 12, label: "в очереди", href: KIT }), Stat({ value: 3, label: "на проверке", href: KIT }), Stat({ value: 40, label: "пропущена", href: KIT })], { tiles: true })),
    sec("Meter, Funnel, Bars",
      el("div", { className: "cols" },
        Card({}, Caption("Meter"), row("70%", Meter(70, 100)), row("ok", Meter(3, 10, { color: "var(--ok)" })), row("bad", Meter(1, 10, { color: "var(--bad)" })), row("0", Meter(0, 10)),
          Caption("Funnel"), Funnel([{ label: "Собрано", value: 128, base: null }, { label: "Оценено LLM", value: 96, base: 128 }, { label: "Отправлено", value: 31, base: 96 },
            { label: "Приглашение", value: 3, base: 31, color: "var(--ok)" }, { label: "Отказ", value: 7, base: 31, color: "var(--bad)" }], { note: "% — от предыдущего шага" })),
        Card({}, Caption("Bars"), Bars(Array.from({ length: 14 }, (_, i) => {
          const sent = [2, 4, 1, 0, 5, 3, 6, 2, 0, 1, 4, 7, 3, 2][i];
          const answered = Math.min(sent, [1, 1, 0, 0, 2, 1, 3, 0, 0, 1, 2, 2, 1, 1][i]);
          const inv = i % 5 === 0 ? Math.min(1, answered) : 0;
          return { total: Math.max(sent, answered), title: `день ${i + 1}: отправлено ${sent}`, parts: [{ value: inv, tone: "ok" as const }, { value: answered - inv, tone: "accent" as const }] };
        }), { from: "5 нояб.", to: "сегодня", legend: [{ label: "отправлено", color: "var(--bar)" }, { label: "ответили отказом", color: "var(--accent)" }, { label: "приглашение", color: "var(--ok)" }] }),
        Caption("RankList"), RankList([["оценка N ниже N", 5], ["стоп-слово «…»", 3], ["LLM", 1]])))),
    sec("FilterList, PickRow, SelectionBar",
      el("div", { className: "cols narrow-left" },
        Card({}, FilterList([{ href: KIT, label: "Все", count: 12, active: true }, { href: KIT, label: "Стек не совпадает", count: 7 }, { href: KIT, label: "Офис / переезд", count: 3 }, { href: KIT, label: "Прочее", count: 2 }], { max: 12, label: "Группы" })),
        Card({}, pickBar, picks))),
    sec("StackTable",
      Card({}, StackTable([{ title: "Поиск" }, { title: "Собрано", num: true }, { title: "Отправлено", num: true }, { title: "Конверсия", num: true }, { title: "" }],
        [["dotnet-remote", 20, 3, "15%", Button("Открыть", { size: "sm" })], ["backend-msk", 3, 0, "0%", Button("Открыть", { size: "sm" })]]),
      el("p", { className: "muted small", textContent: "На ширине ≤ 720px строки становятся карточками «поле: значение»." }))),
    sec("Field, Form",
      Form({ onSubmit: () => Toast("Форма отправлена (витрина: запроса нет)", { tone: "ok" }), submit: null },
        FormGrid(Field("Название", el("input", { value: "dotnet-remote" })), Field("Запрос", el("input", { placeholder: "C# OR .NET" })),
          Field("Включён", el("input", { type: "checkbox", checked: true })), Field("Выключено", el("input", { value: "только чтение", disabled: true })),
          Field("Формат", el("select", {}, options([["", "любой"], ["remote", "удалённо"]]))), Field("Описание (wide)", el("textarea", { value: "Поле на всю ширину" }), { wide: true })),
        Field("Подпись над полем (stacked)", el("textarea", { placeholder: "Черновик ответа" }), { stacked: true }),
        Actions([el("input", { placeholder: "Причина (необязательно)", "aria-label": "Причина" }), Button("Сохранить", { kind: "primary", type: "submit" }), Result("сохранено")]))),
    sec("Тексты",
      Callout("Callout — вывод LLM или итог разбора."), Quote("Quote — сообщение работодателя как есть."), Note("Note — служебная пометка (демо, задел)."),
      el("p", {}, "Secret: ", Secret("hh_pat_xxxxxxxx")),
      Disclosure("Disclosure — раскрывающийся блок", Pre("Pre — длинный текст\nс переносами строк.")), Meta(["Meta", "части через точку", "пустые пропускаются", null])),
    sec("Toast, Confirm, Sheet, Menu",
      row("Toast", Button("Итог", { onClick: () => { Toast("«C# разработчик»: в очереди", { tone: "ok" }); } }),
        Button("С «Отменить»", { onClick: () => { Toast("Вакансия скрыта", { undo: () => Toast("Возвращена") }); } }),
        Button("Ошибка", { kind: "danger", onClick: () => { Toast("Сервер ответил 500", { tone: "bad" }); } })),
      row("Confirm", Button("Удалить поиск", { kind: "danger", onClick: async () => {
        Toast(await Confirm({ title: "Удалить поиск «dotnet-remote»?", text: "Вакансии останутся, правила поиска — нет. Вернуть нельзя.", confirm: "Удалить", kind: "danger" }) ? "Подтверждено" : "Отменено");
      } }), Button("Задать пароль", { onClick: async () => {
        const field = el("input", { type: "password", required: true, minLength: 10, autocomplete: "new-password", "aria-label": "Новый пароль (не короче 10 символов)" });
        Toast(await Confirm({ title: "Пароль для anna@example.com", confirm: "Задать", field }) ? `Пароль из ${field.value.length} символов` : "Отменено");
      } })),
      row("Sheet", sheetButton),
      row("Menu", menu),
      row("Menu (button)", menuButton),
      row("KeyHints", KeyHints([["J/K", "карточки"], ["A", "одобрить"], ["R", "отклонить"], ["E", "письмо"]]))),
    // Шапка и палитра команд (#128): поле поиска открывает палитру (в витрине — без Ctrl+K: он у палитры шапки)
    sec("SearchButton, CommandPalette, Alert",
      row("SearchButton", SearchButton("Найти вакансию, команду…", { onClick: () => palette.open() }), compactSearch),
      row("Alert", el("span", {}, "LLM", Alert({ tone: "warn", title: "Groq на паузе до 15:20" })), el("span", {}, "LLM", Alert({ tone: "bad", title: "сбой связи через VPN" })),
        Tabs([{ href: KIT, label: "Проверка", count: 2 }, { href: KIT, label: "LLM", active: true, alert: { tone: "warn", title: "пауза провайдера" } }], { label: "Разделы со знаком" }))),
    sec("EmptyState, Skeleton",
      el("div", { className: "cols" },
        Card({}, EmptyState("На проверке ничего нет.", { icon: "check" }), EmptyState("Без иконки — как в разделах.")),
        Card({}, Skeleton({ block: true, lines: 3 })))));
}

export async function kit(route: Route): Promise<Child> {
  if (me().role !== "Admin") return [EmptyState("Раздел только для администратора.")];
  if (route.sub === "frame") return [PageHead("UI kit", "Витрина в рамке"), kitGallery()];

  const body = el("div");
  function draw() {
    // Листы прошлой отрисовки витрины — прочь
    document.querySelectorAll<SheetElement>(".sheet.kit-demo").forEach((n) => n.dispose());
    document.querySelectorAll<MenuElement>(".menu.kit-demo").forEach((n) => n.dispose());
    document.querySelectorAll<SelectionBarElement>(".selbar.kit-demo").forEach((n) => n.dispose());
    document.querySelectorAll<PaletteElement>(".palette-backdrop.kit-demo").forEach((n) => n.dispose());
    if (kitState.width !== "auto") {
      body.replaceChildren(el("iframe", { className: "kit-frame", src: path("kit", {}, "frame"), title: `Витрина шириной ${kitState.width}px`, style: `max-width:${kitState.width}px` }));
      return;
    }
    body.replaceChildren(kitState.themes === "both"
      ? el("div", { className: "kit-themes" }, (["dark", "light"] as const).map((t) => el("div", { "data-theme": t }, el("h3", { textContent: t === "dark" ? "Тёмная" : "Светлая" }), kitGallery())))
      : kitGallery());
  }
  const aside = el("div", { className: "bar flush" },
    Segmented<Theme>([{ value: "system", label: "система" }, { value: "dark", label: "тёмная" }, { value: "light", label: "светлая" }],
      currentTheme(), setTheme, { label: "Тема" }),
    Segmented<typeof kitState.themes>([{ value: "one", label: "одна" }, { value: "both", label: "обе рядом" }], kitState.themes, (v) => { kitState.themes = v; draw(); }, { label: "Темы" }),
    Segmented<typeof kitState.width>([{ value: "auto", label: "окно" }, { value: 375, label: "375" }, { value: 720, label: "720" }], kitState.width, (v) => { kitState.width = v; draw(); }, { label: "Ширина" }));
  draw();
  return [PageHead("UI kit", "Компоненты web/src/ui/ui.ts во всех состояниях · данные выдуманные · только для администратора", aside), body];
}
