// Файлы, которые сборка (web/scripts/build.mjs) кладёт рядом с отпечатком в имени: импорт отдаёт их адрес (/icons.[hash].svg)
declare module "*.svg" {
  const url: string;
  export default url;
}
