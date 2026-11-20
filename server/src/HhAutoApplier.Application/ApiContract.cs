namespace HhAutoApplier.Application;

// Контракт API с расширением (#77). Номер растёт при несовместимом изменении: удалили или переименовали поле или эндпоинт,
// поменяли смысл статуса. Тот же номер — API_VERSION в extensions/hh/src/api/version.ts, совпадение проверяет extensions/hh/tests/api-version.test.ts
public static class ApiContract
{
    public const int Version = 1;
}
