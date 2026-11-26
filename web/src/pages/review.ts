// Ручная проверка (#25, #58): вакансии, в которых LLM сомневается, и анкеты с текстовыми ответами LLM.
// «Одобрить» — в очередь с письмом и ответами из полей, «Отклонить» — пропуск с причиной
import { api, post, Unauthorized } from "../api/http";
import type { Question, Vacancy } from "../api/types";
import { options, responsesPill, salary, vacancyRegions } from "../format";
import { act, app, readOnly, scoped, setStatus } from "../state";
import { Actions, Button, Callout, Card, Disclosure, el, EmptyState, Field, KeyHints, Meta, PageHead, Pre, Result, safeUrl, type Child } from "@hh-autoapplier/ui";
import { bindReviewKeys, REVIEW_KEY_HINTS } from "./review-keys";
import { letterRequestLine, letterRequestsBlock, vacancyPath } from "./vacancies";

export async function review(): Promise<Child> {
  const items = (await api<Vacancy[]>(scoped("/api/vacancies?status=Review&take=200"))).sort((a, b) => b.score - a.score);
  const keys = items.length > 0 && !readOnly();
  const list = el("div", { className: "review-list" }, items.length ? items.map(reviewCard) : EmptyState("На проверке ничего нет."));
  if (keys) bindReviewKeys(list);
  return [
    PageHead("Проверка", `Вакансии, в которых LLM сомневается, и анкеты с ответами LLM: ${items.length}. Письмо и текстовые ответы можно поправить перед одобрением.`,
      keys ? KeyHints(REVIEW_KEY_HINTS, { label: "Клавиши проверки" }) : undefined),
    list,
  ];
}

interface AnswerField {
  label: HTMLElement;
  node: HTMLElement;
  // Что выключать на время запроса
  inputs: (HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement)[];
  // Ответ для API
  value: () => string[];
}

// Поле ответа на вопрос анкеты
function answerField(v: Vacancy, q: Question, i: number): AnswerField | null {
  const id = `answer-${v.id}-${i}`;
  const label = el("label", { className: "field", for: id, textContent: q.question });
  if (q.type === "Text") {
    const input = el("textarea", { value: q.answer.join("\n"), id, disabled: readOnly(), "aria-label": q.question });
    return { label, node: input, inputs: [input], value: () => [input.value.trim()] };
  }
  if (q.type === "Radio") {
    const select = el("select", { id, disabled: readOnly(), "aria-label": q.question }, options([["", "— не выбрано —"], ...q.options.map((o): [string, string] => [o, o])]));
    select.value = q.options.includes(q.answer[0]) ? q.answer[0] : "";
    return { label, node: select, inputs: [select], value: () => (select.value ? [select.value] : []) };
  }
  if (q.type === "Checkbox") {
    const boxes = q.options.map((o, k) => el("input", { type: "checkbox", id: `${id}-${k}`, value: o, checked: q.answer.includes(o), disabled: readOnly() }));
    const node = el("div", { role: "group", "aria-label": q.question },
      boxes.map((b) => el("div", {}, b, " ", el("label", { for: b.id, textContent: b.value }))));
    return { label: el("b", { textContent: q.question }), node, inputs: boxes, value: () => boxes.filter((b) => b.checked).map((b) => b.value) };
  }
  return null;
}

// Ответ поля совпадает с ответом LLM: текст — без пробелов по краям, варианты — тем же набором
const sameAnswer = (value: string[], q: Question) => q.type === "Text"
  ? (value[0] ?? "") === q.answer.join("\n").trim()
  : value.length === q.answer.length && value.every((x) => q.answer.includes(x));

function reviewCard(v: Vacancy): HTMLElement {
  const awaitsLetter = v.letterRequestsState === "Found" || v.letterRequestsState === "Failed";
  const letter = el("textarea", { value: v.coverLetter ?? "", disabled: readOnly(),
    placeholder: awaitsLetter ? "LLM ещё не написала письмо. Без письма одобрить нельзя: шаблон расширения не ответит на просьбы работодателя." : "LLM ещё не написала письмо. Напишите своё или одобрите без письма — расширение возьмёт шаблон." });
  letter.id = `letter-${v.id}`;
  letter.dataset.key = "letter";
  const reason = el("input", { placeholder: "Причина отказа (необязательно)", "aria-label": "Причина отказа" });
  const result = Result();
  // Ответы LLM на анкету (#58) — в полях: уходят работодателю от имени кандидата. Правится каждый вопрос (#115):
  // текст — полем, один вариант — списком, несколько — галочками. Нераспознанный вопрос (Unknown) — только показ
  const questions = v.questions ?? [];
  const answered = questions.some((q) => q.answer.length);
  const answerFields = questions.map((q, i) => (answered ? answerField(v, q, i) : null));
  const fields = () => [letter, ...answerFields.flatMap((f) => f?.inputs ?? [])];

  // Кодовое слово вставляется только по подтверждению, значения из профиля — по кнопке после его заполнения. Ссылки и контакты не выполняются
  const resolveRequests = (confirmCodeWords: number[]) => act(() => post<Vacancy>(vacancyPath(v, "letter-requests/resolve"), { confirmCodeWords }),
    (updated) => { node.replaceWith(reviewCard(updated)); return `«${v.title}»: письмо обновлено`; });
  const requestRows = (v.letterRequests ?? []).map((r, i) => {
    if (readOnly() || r.closed || r.type !== "CodeWord" || !r.value) return letterRequestLine(r);
    return el("div", {}, letterRequestLine(r), Actions([Button(`Вставить слово «${r.value}»`, { onClick: () => resolveRequests([i]) })]));
  });
  const needsProfile = (v.letterRequests ?? []).some((r) => !r.closed && (r.type === "Salary" || r.type === "TestTask"));
  const refresh = needsProfile && !readOnly() && v.letterRequestsState === "Found" ? Button("Подставить из профиля", { onClick: () => resolveRequests([]) }) : null;

  // Карточка после решения остаётся на месте с итогом: если убрать её, следующая подъедет под курсор и лишний клик решит за неё
  async function decide(body: object, done: string) {
    approve.disabled = decline.disabled = reason.disabled = true;
    fields().forEach((f) => (f.disabled = true));
    try {
      await post(vacancyPath(v, "review"), body);
      Card.decide(node);
      result.textContent = done;
      setStatus(`«${v.title}»: ${done}. Без решения: ${document.querySelectorAll("#view article.card:not(.decided)").length}`);
    } catch (e) {
      if (e instanceof Unauthorized) app.showLogin();
      setStatus(`«${v.title}»: ${(e as Error).message}`, true);
      approve.disabled = decline.disabled = reason.disabled = false;
      fields().forEach((f) => (f.disabled = false));
    }
  }

  // Письмо уходит, только если его поправили: иначе остаётся письмо LLM с её моделью.
  // Ответы анкеты — все по порядку вопросов, если поправлен хоть один; вариант не из списка или пустой выбор сервер отклонит (409)
  const approve = Button("Одобрить", { kind: "ok", onClick: () => {
    const text = letter.value.trim();
    const edited = answerFields.some((f, i) => f && !sameAnswer(f.value(), questions[i]));
    const answers = edited ? questions.map((q, i) => answerFields[i]?.value() ?? q.answer) : null;
    decide({ approve: true, coverLetter: text && text !== (v.coverLetter ?? "").trim() ? text : null, answers }, "одобрено, в очереди");
  } });
  const decline = Button("Отклонить", { kind: "danger", onClick: () => { decide({ approve: false, reason: reason.value.trim() || null }, "отклонено"); } });

  approve.dataset.key = "approve";
  decline.dataset.key = "decline";
  // tabIndex -1 — карточку можно сделать текущей клавишами J/K и кликом
  const node = Card({ as: "article", tabIndex: -1,
    head: el("h2", {}, el("a", { href: safeUrl(v.url), target: "_blank", rel: "noopener", textContent: v.title }), responsesPill(v) ? [" ", responsesPill(v)] : null),
    meta: [v.company, vacancyRegions(v), salary(v) ?? "зарплата не указана", v.workFormat, v.workExperience && `опыт: ${v.workExperience}`, `балл ${v.score}`, v.resumeProfile && `резюме ${v.resumeProfile}`, v.hasTest && "есть тест"],
    actions: readOnly() ? null : [approve, reason, decline, result] },
  Callout(v.statusReason ?? v.llmReason ?? "LLM сомневается"),
  v.stack?.length ? Meta(`Стек: ${v.stack.join(", ")}`) : null,
  Disclosure("Описание вакансии", Pre(v.description || "описания нет", { scroll: true })),
  questions.length ? Disclosure(`Анкета (${questions.length})`,
    questions.map((q, i) => {
      const f = answerFields[i];
      return f
        ? el("div", { className: "reason" }, f.label, f.node)
        : el("div", { className: "reason" }, el("b", { textContent: q.question }), el("br"), q.answer.length ? q.answer.join("; ") : "ответа нет");
    }), { open: answered }) : null,
  v.letterRequestsState === "Failed" ? letterRequestsBlock(v) : null,
  requestRows.length ? Disclosure(`Просьбы к письму (${requestRows.length})`, [requestRows, refresh ? Actions([refresh]) : null], { open: true }) : null,
  Field(v.coverLetterModel ? `Письмо (${v.coverLetterModel === "manual" ? "ваше" : v.coverLetterModel})` : "Письмо", letter, { stacked: true }));
  return node;
}
