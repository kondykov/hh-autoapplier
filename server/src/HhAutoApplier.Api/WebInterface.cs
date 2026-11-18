using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace HhAutoApplier.Api;

/// <summary>
/// Веб-интерфейс (#46, #130): сборка web/ (npm run build:web) в wwwroot. Страницы — без входа: данных в них нет, вход они
/// спрашивают сами. Разделы — на путях (/vacancies?status=Queued), поэтому index.html отдаётся на любой путь без расширения,
/// кроме API, /health и документации API: неизвестный /api/... остаётся 404, а не страницей.
/// </summary>
public static partial class WebInterface
{
    // Всё, что не начинается с путей сервера (API, проверка живости, документация API). Регистр ограничение regex
    // маршрута не учитывает: /API/x — тоже не страница
    private const string NotServer = "^(?!(api|health|openapi|scalar)(/|$)).*$";

    // Отпечаток содержимого в имени (app.5PWVXN45.js): файл с таким именем не меняется — кэш на год.
    // Остальное (index.html и старые review.html/dialogs.html) — с проверкой при каждом открытии: так браузер узнаёт новую сборку
    [GeneratedRegex(@"\.[A-Z0-9]{8}\.[a-z0-9]+(\.map)?$")]
    private static partial Regex Fingerprinted();

    private static readonly StaticFileOptions Files = new()
    {
        OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] =
            Fingerprinted().IsMatch(context.File.Name) ? "public, max-age=31536000, immutable" : "no-cache",
    };

    /// <summary>Статика wwwroot; «/» — index.html.</summary>
    public static WebApplication UseWebInterface(this WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles(Files);
        return app;
    }

    /// <summary>index.html на пути разделов: обновление страницы на /vacancies открывает тот же раздел.</summary>
    public static WebApplication MapWebInterface(this WebApplication app)
    {
        // nonfile — путь без расширения (файлы отдаёт статика, отсутствующий файл — 404); fallback — после всех маршрутов API
        app.MapFallbackToFile($"{{*path:nonfile:regex({NotServer})}}", "index.html", Files).AllowAnonymous();
        return app;
    }
}
