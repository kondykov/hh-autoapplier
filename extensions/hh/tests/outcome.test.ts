import { beforeEach, describe, expect, it } from "vitest";
import { detectApplyOutcome, isAlreadyRespondedOnPage, isOutlineDetected } from "../src/apply/outcome-detector";

describe("outcome detector", () => {
  beforeEach(() => {
    document.body.innerHTML = "";
  });

  it("detects outline in modal", () => {
    document.body.innerHTML = `<div data-qa="vacancy-response-popup">Для отклика необходимо ответить на несколько вопросов</div>`;
    expect(isOutlineDetected()).toBe(true);
    expect(detectApplyOutcome()).toBe("outline");
  });

  it("detects regular modal", () => {
    document.body.innerHTML = `<div data-qa="vacancy-response-popup">Резюме</div>`;
    expect(detectApplyOutcome()).toBe("modal");
  });

  it("detects letter place", () => {
    document.body.innerHTML = `<button data-qa="vacancy-response-letter-toggle">Приложить письмо</button>`;
    expect(detectApplyOutcome()).toBe("letter");
  });

  it("ignores response markers in similar vacancies and text outside the current response controls", () => {
    document.body.innerHTML = `<main>
      <button data-qa="vacancy-response-link-top">Откликнуться</button>
      <div data-qa="vacancy-description">Вы откликнулись на похожую вакансию?</div>
      <article data-qa="vacancy-serp__vacancy">
        <span data-qa="vacancy-serp__vacancy_responded">Вы откликнулись</span>
        <span data-qa="already-responded-text">Вы откликнулись</span>
      </article>
    </main>`;
    expect(isAlreadyRespondedOnPage()).toBe(false);
  });

  it("accepts response text inside the current vacancy action", () => {
    document.body.innerHTML = `<button data-qa="vacancy-response-link-top">Вы откликнулись</button>`;
    expect(isAlreadyRespondedOnPage()).toBe(true);
  });

  it("returns null on empty page", () => {
    expect(detectApplyOutcome()).toBeNull();
  });
});
