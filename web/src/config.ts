// Флаги веба (#128): функции, которые собраны, но без готового backend в релизе не включаются
export const features = {
  // Поиск по вакансиям в палитре Ctrl+K — серверный, GET /api/vacancies?q= (#141). Выключить — если backend без q
  vacancySearch: true,
};
