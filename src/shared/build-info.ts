declare const __VERSION__: string | undefined;

// Версия сборки (scripts/build.mjs берёт её из manifest.json); в тестах сборки нет
export const VERSION = typeof __VERSION__ === "string" ? __VERSION__ : "dev";
