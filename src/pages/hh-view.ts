import type { ViewOptions } from "../shared/panel/view";

// Подписи общей панели для hh.ru (#123): знак бота и имя сайта
export const HH_VIEW: Pick<ViewOptions, "brand" | "site"> = { brand: ["hh", "=#"], site: "hh.ru" };

export const HH_HOME = "https://hh.ru/";
