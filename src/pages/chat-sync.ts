import type { KeyValueStorage } from "../infra/storage";

// «Синхронизировать чаты» с любой страницы (#123): панель открывает страницу откликов hh.ru и оставляет просьбу,
// страница чатов после загрузки её забирает и запускает сбор. Просьба живёт недолго: старая — не повод слать чаты
const KEY = "hh_chat_sync_request";
export const CHAT_SYNC_TTL_MS = 2 * 60 * 1000;

// «Отклики» соискателя: страница со списком чатов, тип страницы — chat
export const chatsPageUrl = (origin = "https://hh.ru") => `${origin}/applicant/negotiations`;

export const requestChatSync = (storage: KeyValueStorage, now = Date.now()) => storage.set(KEY, now);

// Свежая просьба есть — забрать (одна загрузка страницы — один сбор)
export async function takeChatSync(storage: KeyValueStorage, now = Date.now()): Promise<boolean> {
  const at = await storage.get<number | null>(KEY, null);
  if (at === null) return false;
  await storage.set(KEY, null);
  return now - at >= 0 && now - at <= CHAT_SYNC_TTL_MS;
}
