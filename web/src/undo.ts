// Отмена тостом (#128): действие с обратным в API выполняется сразу, без подтверждения, а тост 6 с даёт «Отменить».
// Ошибка действия или отмены — в строке статуса (act), как у остальных действий
import { act } from "./state";
import { Toast } from "./ui/ui";

export interface UndoOptions<T> {
  // Текст тоста после действия; функция — по ответу сервера
  done: string | ((result: T) => string);
  // Обратное действие: вызывается не больше одного раза
  undo: () => Promise<unknown>;
  // Текст тоста после отмены
  undone: string;
  // Что вернуть на странице после удачной отмены (карточку, переключатель)
  onUndone?: () => void;
}

// Выполнить действие и показать тост с «Отменить»; undefined — действие не удалось (тоста нет)
export async function undoable<T>(action: () => Promise<T>, { done, undo, undone, onUndone }: UndoOptions<T>): Promise<T | undefined> {
  const result = await act(action);
  if (result === undefined) return undefined;
  Toast(typeof done === "function" ? done(result) : done, {
    tone: "ok",
    undo: async () => {
      if ((await act(undo)) === undefined) return;
      onUndone?.();
      Toast(undone);
    },
  });
  return result;
}
