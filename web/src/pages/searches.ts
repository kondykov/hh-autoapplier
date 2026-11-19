// Поиски: сервер собирает вакансии по включённым поискам и отбирает по правилам
import { api, del, put } from "../api/http";
import type { Search } from "../api/types";
import { lines } from "../format";
import { act, app, readOnly, scoped } from "../state";
import { Button, Confirm, el, Field, Form, FormGrid, PageHead, Toolbar, type Child } from "../ui/ui";

export async function searches(): Promise<Child> {
  const items = await api<Search[]>(scoped("/api/searches"));
  const list = items.map((s) => searchForm(s));
  if (!readOnly()) list.push(searchForm(null));
  return [
    PageHead("Поиски", "Сервер собирает вакансии по включённым поискам раз в час и отбирает по правилам; остальное оценивает LLM."),
    list,
  ];
}

function searchForm(s: Search | null): HTMLFormElement {
  const r = s?.rules ?? {};
  const inputs = {
    name: el("input", { value: s?.name ?? "", required: true }),
    query: el("input", { value: s?.query ?? "", required: true }),
    area: el("input", { value: s?.area ?? "", placeholder: "113 — Россия, 1 — Москва" }),
    extraParams: el("input", { value: s?.extraParams ?? "", placeholder: "work_format=REMOTE&experience=between3And6" }),
    enabled: el("input", { type: "checkbox", checked: s?.enabled ?? true }),
    includeKeywords: el("input", { value: (r.includeKeywords ?? []).join(", ") }),
    excludeKeywords: el("input", { value: (r.excludeKeywords ?? []).join(", ") }),
    levelKeywords: el("input", { value: (r.levelKeywords ?? []).join(", "), placeholder: "junior, младший", title: "Ищутся только в названии. Отсев — только без опыта или при стажировке, иначе решает LLM" }),
    allowedFormats: el("input", { value: (r.allowedFormats ?? []).join(", "), placeholder: "Удалённо, Гибрид" }),
    minSalaryRub: el("input", { type: "number", value: r.minSalaryRub ?? "" }),
    minScore: el("input", { type: "number", value: r.minScore ?? 0 }),
    requireSalary: el("input", { type: "checkbox", checked: !!r.requireSalary }),
    skipWithTest: el("input", { type: "checkbox", checked: !!r.skipWithTest }),
    skipReview: el("input", { type: "checkbox", checked: !!r.skipReview }),
  };
  if (readOnly()) Object.values(inputs).forEach((i) => (i.disabled = true));

  const save = Button(s ? "Сохранить" : "Добавить", { kind: "primary", type: "submit" });
  const remove = s ? Button("Удалить", { kind: "danger", onClick: async () => {
    if (await Confirm({ title: `Удалить поиск «${s.name}»?`, text: "Сбор по нему прекратится, правила пропадут. Собранные вакансии останутся. Вернуть нельзя.", confirm: "Удалить", kind: "danger" }))
      return act(() => del(`/api/searches/${s.id}`), () => { app.render(); return `Поиск «${s.name}» удалён`; });
  } }) : null;

  return Form({ submit: save, onSubmit: () => act(() => put<Search>("/api/searches", {
    name: inputs.name.value.trim(), query: inputs.query.value.trim(), area: inputs.area.value.trim() || null,
    extraParams: inputs.extraParams.value.trim() || null, enabled: inputs.enabled.checked,
    rules: {
      includeKeywords: lines(inputs.includeKeywords.value), excludeKeywords: lines(inputs.excludeKeywords.value), levelKeywords: lines(inputs.levelKeywords.value),
      allowedFormats: lines(inputs.allowedFormats.value), minSalaryRub: inputs.minSalaryRub.value ? Number(inputs.minSalaryRub.value) : null,
      minScore: Number(inputs.minScore.value || 0), requireSalary: inputs.requireSalary.checked,
      skipWithTest: inputs.skipWithTest.checked, skipReview: inputs.skipReview.checked,
    },
  }), (saved) => { app.render(); return `Поиск «${saved.name}» сохранён`; }) },
  el("h2", { textContent: s ? s.name : "Новый поиск" }),
  FormGrid(
    Field("Название", inputs.name), Field("Запрос", inputs.query), Field("Регион (area)", inputs.area), Field("Доп. параметры", inputs.extraParams),
    Field("Включён", inputs.enabled), Field("Нужные слова", inputs.includeKeywords), Field("Исключить слова", inputs.excludeKeywords), Field("Слова уровня (в названии)", inputs.levelKeywords),
    Field("Форматы работы", inputs.allowedFormats), Field("Зарплата от, ₽", inputs.minSalaryRub), Field("Мин. балл", inputs.minScore),
    Field("Только с зарплатой", inputs.requireSalary), Field("Пропускать с тестом", inputs.skipWithTest), Field("Без ручной проверки", inputs.skipReview)),
  readOnly() ? null : Toolbar([save, remove], { after: true }));
}
