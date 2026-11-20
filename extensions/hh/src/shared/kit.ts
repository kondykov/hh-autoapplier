import icons from "@hh-autoapplier/ui/icons.svg";
import tokens from "@hh-autoapplier/ui/tokens.css";
import ui from "@hh-autoapplier/ui/ui.css";
import { injectIcons, injectStyles } from "@hh-autoapplier/ui";

// UI kit в расширении (#129, #123): токены, стили и иконки — те же файлы packages/ui, что и в вебе, без копий.
// Сборка (scripts/build.mjs) отдаёт их текстом; тема — по системе, как в вебе (data-theme на хосте или <html> — вручную).
// root — shadow root панели на странице сайта или document страницы расширения; extra — свои стили поверх kit

export function mountKit(root: Document | ShadowRoot, ...extra: string[]): void {
  injectStyles(root, [tokens, ui, ...extra]);
  // Спрайт — внутрь root: на чужом сайте внешний файл спрайта не загрузится
  injectIcons(root, icons);
}
