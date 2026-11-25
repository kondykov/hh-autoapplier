import type { AutoReply } from "../api/client";
import { DELAYS, pause } from "../core/delays";
import type { AppState } from "../core/state";
import { ChatSource, describeWriteReason, pageFetch } from "../dialogs/chats";
import { chatUrl, isChatPage, sendChatMessage, type SendResult } from "../dialogs/reply";
import type { PageType } from "../dom/page-type";
import { pageOf, type PageStep, type StepContext } from "./step-context";

export type ChatReader = Pick<ChatSource, "recent" | "writePossibility">;

interface AutoReplyAttempt {
  outcome?: SendResult;
  reported?: boolean;
}

// «Синхронизировать чаты с сервером» (бывший «Автоответ», #26, #123): чаты откликов → на сервер (статусы, разбор LLM, Telegram);
// на новые отказы — короткое «спасибо», где hh.ru разрешает писать (решение владельца 2026-10-26).
// Обычно после отказа переписка закрыта до приглашения — тогда сервер узнаёт, что автоответ невозможен.
// Пробный режим (галочка очереди) не отправляет ничего работодателю
export class DialogStep implements PageStep {
  readonly name = "синхронизация чатов";

  private running = false;

  constructor(
    private ctx: StepContext,
    private chats: ChatReader = new ChatSource(pageFetch, () => pause(DELAYS.click)),
    private send: (text: string) => Promise<SendResult> = sendChatMessage,
  ) {}

  // Отправка идёт по одному чату на загрузку страницы: каждый чат — отдельный переход
  accepts(page: PageType, state: AppState): boolean {
    return page === "chat" && state.autoReplies.length > 0;
  }

  async run(): Promise<void> {
    const { logger, panel, backend, config, state } = this.ctx;
    if (this.running) return;
    if (pageOf(this.ctx) !== "chat") {
      logger.log("Откройте страницу чатов или откликов hh.ru", "error");
      return;
    }

    this.running = true;
    try {
      panel.setStep("Собираю чаты…");
      let chats;
      try {
        chats = await this.chats.recent();
      } catch (e) {
        logger.log(`Чаты hh.ru не получены: ${(e as Error).message}`, "error");
        panel.setStep("Синхронизация чатов: ошибка — см. журнал");
        return;
      }

      const res = await backend.importDialogs(chats);
      if (!res.ok) {
        logger.log(`Чаты не отправлены на сервер: ${res.error}`, "error");
        panel.setStep("Синхронизация чатов: ошибка — см. журнал");
        return;
      }
      const r = res.result;
      logger.log(`Чатов: ${r.received}, новых сообщений работодателей: ${r.newMessages}, отказов: ${r.rejected}, приглашений: ${r.invited}${r.recovered ? `, найдено потерянных откликов: ${r.recovered}` : ""}. ` +
        "Разбор и черновики ответов — в Telegram и в веб-интерфейсе backend («Ответы работодателей»)", "success");

      const pending: AutoReply[] = [];
      for (const reply of r.autoReplies)
        if (!await this.deliverReply(reply)) pending.push(reply);

      // «Спасибо» выключено в настройках (#123): ничего не пишем и сервер не трогаем — включат, ответы уйдут со следующей синхронизацией
      if (!config.thanksOnReject) {
        if (pending.length) logger.log(`«Спасибо» на отказы выключено в настройках — не отвечаю (${pending.length})`);
        panel.setStep("Синхронизация чатов: готово");
        return;
      }

      const writable: AutoReply[] = [];
      for (const reply of pending) {
        const label = reply.title || `чат ${reply.chatId}`;
        let possibility;
        try {
          possibility = await this.chats.writePossibility(reply.chatId);
        } catch (e) {
          logger.log(`«${label}»: не узнать, можно ли писать (${(e as Error).message}) — попробую в следующий раз`, "warn");
          continue;
        }
        if (!possibility.canWrite) {
          const reason = describeWriteReason(possibility.reason);
          await this.deliverReply(reply, async () => ({ ok: false, error: reason }));
          continue;
        }
        if (config.queueDryRun) {
          logger.log(`Пробный режим: «${label}» — ответ на отказ не отправлен`, "warn");
          continue;
        }
        writable.push(reply);
      }

      if (writable.length === 0) {
        panel.setStep("Синхронизация чатов: готово");
        return;
      }
      state.autoReplies = writable;
      await this.ctx.persist();
      logger.log(`Отвечаю на отказы: ${writable.length}`, "step");
      this.ctx.nav.go(chatUrl(writable[0].chatId, new URL(this.ctx.nav.url()).origin));
    } finally {
      this.running = false;
    }
  }

  // Открыт чат первого автоответа: отправить, сообщить итог, перейти к следующему
  async resume(): Promise<void> {
    const { state, logger, panel, config, nav } = this.ctx;
    const current = state.autoReplies[0];
    if (!current) return;
    // Пробный режим включили, пока шли переходы, — не отправлять
    if (config.queueDryRun) {
      state.autoReplies = [];
      await this.ctx.persist();
      logger.log("Пробный режим: автоответы остановлены", "warn");
      return;
    }
    // «Спасибо» выключили в настройках, пока шли переходы
    if (!config.thanksOnReject) {
      state.autoReplies = [];
      await this.ctx.persist();
      logger.log("«Спасибо» на отказы выключено в настройках: автоответы остановлены", "warn");
      return;
    }

    // Не тот чат (hh.ru перенаправил) — не писать в чужой
    await this.deliverReply(current, async () => isChatPage(nav.url(), current.chatId)
      ? this.send(config.rejectionReply)
      : { ok: false, error: "чат не открылся" });

    if (state.autoReplies[0] === current) state.autoReplies = state.autoReplies.slice(1);
    await this.ctx.persist();
    const next = state.autoReplies[0];
    if (!next) {
      panel.setStep("Синхронизация чатов: готово");
      return;
    }
    await pause(DELAYS.step);
    nav.go(chatUrl(next.chatId, new URL(nav.url()).origin));
  }

  // Запись до клика переживает перезагрузку. Блокировка события не даёт двум вкладкам одного hh.ru кликнуть одновременно.
  private async deliverReply(reply: AutoReply, send?: () => Promise<SendResult>): Promise<boolean> {
    const { storage, backend, logger } = this.ctx;
    const key = `hh_auto_reply:${reply.chatId}:${reply.messageId ?? "legacy"}`;
    const deliver = async (): Promise<boolean> => {
      const previous = await storage.get<AutoReplyAttempt | null>(key, null);
      if (previous?.reported) return true;
      if (!previous && !send) return false;

      let outcome = previous?.outcome;
      if (!previous && send) {
        await storage.set<AutoReplyAttempt>(key, {});
        outcome = await send();
      }
      // Вкладка исчезла между кликом и подтверждением отправки. Повтор мог бы написать работодателю дважды.
      outcome ??= { ok: false, error: "результат отправки неизвестен — проверьте чат hh.ru" };
      if (!previous?.outcome) await storage.set<AutoReplyAttempt>(key, { outcome });

      const label = reply.title || `чат ${reply.chatId}`;
      const error = outcome.ok ? undefined : outcome.error;
      if (!previous?.outcome)
        logger.log(outcome.ok ? `«${label}»: ответ на отказ отправлен` : `«${label}»: ${error}`, outcome.ok ? "success" : "warn");
      const result = reply.messageId === undefined
        ? await backend.reportAutoReply(reply.chatId, outcome.ok, error)
        : await backend.reportAutoReply(reply.chatId, outcome.ok, error, reply.messageId);
      if (result.ok) await storage.set<AutoReplyAttempt>(key, { reported: true });
      else logger.log(`«${label}»: результат автоответа ждёт отправки на сервер — ${result.error}`, "warn");
      return true;
    };
    return navigator.locks ? navigator.locks.request(key, deliver) : deliver();
  }
}
