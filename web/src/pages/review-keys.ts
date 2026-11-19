// Клавиши «Проверки» (#128): J/K — следующая/предыдущая карточка, A — одобрить, R — отклонить, E — к письму.
// Клавиша — по физической кнопке (code), поэтому работает и в русской раскладке. Не срабатывают в полях ввода,
// с Ctrl/Alt/Cmd и при открытом диалоге
export type ReviewKey = "next" | "prev" | "approve" | "decline" | "letter";

const CODES: Record<string, ReviewKey> = { KeyJ: "next", KeyK: "prev", KeyA: "approve", KeyR: "decline", KeyE: "letter" };

export const REVIEW_KEY_HINTS: [string, string][] = [["J/K", "карточки"], ["A", "одобрить"], ["R", "отклонить"], ["E", "письмо"]];

// Фокус в поле: набор текста, а не команда
export function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT";
}

export function reviewKey(e: Pick<KeyboardEvent, "code" | "ctrlKey" | "metaKey" | "altKey" | "target">): ReviewKey | null {
  if (e.ctrlKey || e.metaKey || e.altKey || isTyping(e.target)) return null;
  if (document.querySelector(".dialog-backdrop")) return null;
  return CODES[e.code] ?? null;
}

// Карточки списка: data-key="approve" | "decline" — кнопки, "letter" — поле письма
export function bindReviewKeys(list: HTMLElement): void {
  const cards = () => [...list.querySelectorAll<HTMLElement>("article.card")];
  const current = () => list.querySelector<HTMLElement>("article.card.current");
  const select = (card: HTMLElement | undefined) => {
    if (!card) return;
    current()?.classList.remove("current");
    card.classList.add("current");
    card.focus({ preventScroll: true });
    card.scrollIntoView?.({ block: "start", behavior: "smooth" });
  };
  const move = (step: number) => {
    const all = cards();
    const at = all.indexOf(current() as HTMLElement);
    select(all[at < 0 ? (step > 0 ? 0 : all.length - 1) : Math.min(all.length - 1, Math.max(0, at + step))]);
  };
  // Клик по карточке (или фокус в её поле) делает её текущей
  list.addEventListener("focusin", (e) => {
    const card = (e.target as Element).closest?.<HTMLElement>("article.card");
    if (card && !card.classList.contains("current")) { current()?.classList.remove("current"); card.classList.add("current"); }
  });

  const onKey = (e: KeyboardEvent) => {
    // Раздел закрыт — слушатель больше не нужен
    if (!list.isConnected) { document.removeEventListener("keydown", onKey); return; }
    const key = reviewKey(e);
    if (!key) return;
    e.preventDefault();
    if (key === "next") return move(1);
    if (key === "prev") return move(-1);
    const card = current();
    if (!card) return move(1);
    if (key === "letter") return card.querySelector<HTMLElement>("[data-key=letter]")?.focus();
    const button = card.querySelector<HTMLButtonElement>(`[data-key=${key}]`);
    if (!button || button.disabled) return;
    button.click();
    // Решили — к следующей нерешённой карточке
    const next = cards().slice(cards().indexOf(card) + 1).find((c) => !c.classList.contains("decided"));
    if (next) select(next);
  };
  document.addEventListener("keydown", onKey);
}
