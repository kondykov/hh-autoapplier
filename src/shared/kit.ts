import icons from "../../web/src/ui/icons.svg";
import tokens from "../../web/src/ui/tokens.css";
import ui from "../../web/src/ui/ui.css";
import { injectIcons, injectStyles } from "../../web/src/ui/ui";

// UI kit в расширении (#129, #123): токены, стили и иконки — те же файлы web/src/ui, что и в вебе, без копий.
// Сборка (scripts/build.mjs) отдаёт их текстом; тема — по системе, как в вебе (data-theme на хосте или <html> — вручную).
// root — shadow root панели на странице сайта или document страницы расширения; extra — свои стили поверх kit

export function mountKit(root: Document | ShadowRoot, ...extra: string[]): void {
  injectStyles(root, [tokens, ui, ...extra]);
  // Спрайт — внутрь root: на чужом сайте внешний файл спрайта не загрузится
  injectIcons(root, icons);
}
