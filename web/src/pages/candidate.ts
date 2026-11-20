// Кандидат (#128, бывший раздел «Резюме»): профиль кандидата и профили резюме
import { api, put } from "../api/http";
import type { Profile, Resume } from "../api/types";
import { lines, options } from "../format";
import { act, app, readOnly, scoped } from "../state";
import { Actions, Button, Card, el, Field, Form, FormGrid, PageHead, Toolbar, type Child } from "../ui/ui";

export async function candidate(): Promise<Child> {
  const [items, profile] = await Promise.all([api<Resume[]>(scoped("/api/resumes")), api<Profile>(scoped("/api/profile"))]);
  const list = items.map((r) => resumeForm(r));
  if (!readOnly()) list.push(resumeForm(null));
  return [
    PageHead("Кандидат", "Профиль кандидата — общий для всех резюме. Профили резюме: «совпадение» — часть названия резюме на hh.ru, по ней расширение выбирает резюме. Описание и шаблон письма читает LLM."),
    candidateForm(profile),
    list,
  ];
}

type SkillRow = HTMLElement & { read(): { skill: string; facts: string } };

// Профиль кандидата (#35): общая часть и факты по навыкам — общие для всех резюме.
// Под вакансию LLM получает общую часть и факты только по навыкам этой вакансии
function candidateForm(profile: Profile): HTMLFormElement {
  const general = el("textarea", { value: profile.general ?? "", disabled: readOnly(),
    placeholder: "Уровень, стаж, формат работы, город, языки, пожелания" });
  // Для ответов на просьбы работодателя (#72): LLM их не получает, в письмо дописывает сервер
  const desiredSalary = el("input", { type: "number", min: "1", step: "1", value: profile.desiredSalaryRub ?? "", disabled: readOnly(), placeholder: "Например, 150000", "aria-label": "Желаемая зарплата на руки, ₽" });
  const readyForTest = el("select", { disabled: readOnly(), "aria-label": "Готовность к тестовому заданию" }, options([["", "не указано"], ["true", "да"], ["false", "нет"]]));
  readyForTest.value = profile.readyForTestTask == null ? "" : String(profile.readyForTestTask);
  const rows = el("div");

  function skillRow(s = { skill: "", facts: "" }): SkillRow {
    const skill = el("input", { value: s.skill, placeholder: "Навык, синонимы через «/»: EF Core / Entity Framework", "aria-label": "Навык", disabled: readOnly() });
    const facts = el("textarea", { value: s.facts, placeholder: "Где применял, сколько, что делал, цифры", "aria-label": "Факты", disabled: readOnly() });
    facts.style.minHeight = "70px";
    skill.style.width = "100%";
    skill.style.marginBottom = "6px";
    const row = Card({ tight: true }, skill, facts, readOnly() ? null : Actions([Button("Убрать", { kind: "danger", onClick: () => row.remove() })])) as SkillRow;
    row.read = () => ({ skill: skill.value.trim(), facts: facts.value.trim() });
    return row;
  }

  rows.append(...profile.skills.map((s) => skillRow(s)));
  if (!readOnly() && profile.skills.length === 0) rows.append(skillRow());

  const add = Button("+ Навык", { onClick: () => rows.append(skillRow()) });
  const save = Button("Сохранить профиль", { kind: "primary", type: "submit" });
  return Form({ submit: save, onSubmit: () => act(() => put<Profile>("/api/profile", { general: general.value.trim() || null,
    skills: [...rows.children].map((r) => (r as SkillRow).read()),
    desiredSalaryRub: desiredSalary.value ? Number(desiredSalary.value) : null,
    readyForTestTask: readyForTest.value === "" ? null : readyForTest.value === "true" }),
  (saved) => { app.render(); return `Профиль кандидата сохранён: навыков ${saved.skills.length}`; }) },
  el("h2", { textContent: "Профиль кандидата" }),
  el("p", { className: "muted", textContent: "Общий для всех резюме. В письмо, оценку, ответы анкет и разбор ответов LLM получает общую часть и факты только по навыкам вакансии. Пишите только то, что было: это уходит работодателю от вашего имени." }),
  Field("Общая часть", general, { stacked: true }),
  Field("Желаемая зарплата на руки, ₽", desiredSalary, { stacked: true }),
  Field("Готов к тестовому заданию", readyForTest, { stacked: true }),
  el("p", { className: "muted", textContent: "Эти два поля LLM не получает: сервер дописывает их к письму, только если работодатель просит. Пусто — отклик с такой просьбой уйдёт на проверку." }),
  el("label", { className: "field", textContent: `Факты по навыкам (${profile.skills.length})` }), rows,
  readOnly() ? null : Actions([add, save]));
}

function resumeForm(r: Resume | null): HTMLFormElement {
  const inputs = {
    name: el("input", { value: r?.name ?? "", required: true }),
    match: el("input", { value: r?.match ?? "", required: true }),
    keywords: el("input", { value: (r?.keywords ?? []).join(", ") }),
    summary: el("textarea", { value: r?.summary ?? "" }),
    letterTemplate: el("textarea", { value: r?.letterTemplate ?? "" }),
  };
  if (readOnly()) Object.values(inputs).forEach((i) => (i.disabled = true));

  const save = Button(r ? "Сохранить" : "Добавить", { kind: "primary", type: "submit" });
  return Form({ submit: save, onSubmit: () => act(() => put<Resume>("/api/resumes", {
    name: inputs.name.value.trim(), match: inputs.match.value.trim(), keywords: lines(inputs.keywords.value),
    summary: inputs.summary.value.trim() || null, letterTemplate: inputs.letterTemplate.value.trim() || null,
  }), (saved) => { app.render(); return `Резюме «${saved.name}» сохранено`; }) },
  el("h2", { textContent: r ? r.name : "Новый профиль резюме" }),
  FormGrid(
    Field("Название", inputs.name), Field("Совпадение", inputs.match), Field("Ключевые слова", inputs.keywords),
    Field("Описание для LLM", inputs.summary, { wide: true }), Field("Шаблон письма", inputs.letterTemplate, { wide: true })),
  readOnly() ? null : Toolbar([save], { after: true }));
}
