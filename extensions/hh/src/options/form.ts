import { serverOriginPattern } from "../api/transport";
import type { AppConfig } from "../core/config";
import { isPanelMode } from "../shared/panel/model";

// Форма страницы настроек (#123): значения полей как строки, проверка и перенос в конфиг — без DOM, чтобы тестировать
export interface SettingsForm {
  url: string;
  // Пусто — оставить сохранённый токен
  apiKey: string;
  importSearch: string;
  dailyLimit: string;
  queueRunLimit: string;
  rejectionReply: string;
  thanksOnReject: boolean;
  letter: string;
  panelMode: string;
}

export type FormField = keyof SettingsForm;

export type FormResult = { ok: true; config: AppConfig } | { ok: false; errors: Partial<Record<FormField, string>> };

export const toForm = (c: AppConfig): SettingsForm => ({
  url: c.server.url,
  apiKey: "",
  importSearch: c.importSearch,
  dailyLimit: String(c.dailyLimit),
  queueRunLimit: String(c.queueRunLimit),
  rejectionReply: c.rejectionReply,
  thanksOnReject: c.thanksOnReject,
  letter: c.coverLetterTemplate,
  panelMode: c.panelMode,
});

const int = (s: string) => (/^\s*\d+\s*$/.test(s) ? parseInt(s, 10) : NaN);

// Форма поверх свежего конфига: поля, которых на странице нет (пробный режим очереди), не трогаются
export function applyForm(config: AppConfig, form: SettingsForm): FormResult {
  const errors: Partial<Record<FormField, string>> = {};
  const url = form.url.trim().replace(/\/+$/, "");
  if (!serverOriginPattern(url)) errors.url = "Адрес должен начинаться с http:// или https://";
  const dailyLimit = int(form.dailyLimit);
  if (!(dailyLimit >= 1)) errors.dailyLimit = "Целое число от 1";
  const queueRunLimit = int(form.queueRunLimit);
  if (!(queueRunLimit >= 0)) errors.queueRunLimit = "Целое число от 0 (0 — без ограничения)";
  const rejectionReply = form.rejectionReply.trim();
  if (!rejectionReply) errors.rejectionReply = "Текст ответа не может быть пустым";
  const letter = form.letter.trim();
  if (!letter) errors.letter = "Письмо не может быть пустым";
  if (!isPanelMode(form.panelMode)) errors.panelMode = "Неизвестный вид панели";
  if (Object.keys(errors).length) return { ok: false, errors };

  const apiKey = form.apiKey.trim();
  return {
    ok: true,
    config: {
      ...config,
      server: { url, apiKey: apiKey || config.server.apiKey },
      importSearch: form.importSearch.trim(),
      dailyLimit,
      queueRunLimit,
      rejectionReply,
      thanksOnReject: form.thanksOnReject,
      coverLetterTemplate: letter,
      panelMode: isPanelMode(form.panelMode) ? form.panelMode : config.panelMode,
    },
  };
}

// Токен не показываем целиком: только начало и последние символы
export const maskToken = (token: string) => (!token ? "" : token.length <= 8 ? "••••" : `${token.slice(0, 3)}••••${token.slice(-4)}`);
