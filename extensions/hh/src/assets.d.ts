// CSS и SVG в расширении импортируются текстом (scripts/build.mjs): UI kit из web/src/ui и стили панели (#129, #123)
declare module "*.css" {
  const text: string;
  export default text;
}
declare module "*.svg" {
  const text: string;
  export default text;
}
