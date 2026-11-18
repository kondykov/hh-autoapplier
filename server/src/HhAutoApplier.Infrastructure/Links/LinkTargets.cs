using System.Net;
using HhAutoApplier.Domain.Links;

namespace HhAutoApplier.Infrastructure.Links;

// Направление: куда ходит приложение и через какой прокси (null — напрямую)
public sealed record LinkTarget(string Link, string Host, int Port, Uri? Proxy);

// Что проверять (#132): адреса, к которым ходит приложение, и прокси, которым ходят HTTP-клиенты приложения
// (HttpClient.DefaultProxy — HTTPS_PROXY и NO_PROXY). Так проверка идёт тем же путём, что и запросы: ГигаЧат в NO_PROXY —
// напрямую, Telegram и Groq — через VPN
public sealed class LinkTargets
{
    public LinkTargets(IEnumerable<(string Link, Uri Url)> endpoints, IWebProxy? proxy)
    {
        var targets = new List<LinkTarget>();
        foreach (var (link, url) in endpoints.DistinctBy(e => e.Url.Host.ToLowerInvariant()))
            targets.Add(new LinkTarget(link, url.Host, url.Port, ProxyFor(proxy, url)));

        Directions = targets;
        Proxy = targets.Select(t => t.Proxy).FirstOrDefault(p => p is not null);
    }

    public IReadOnlyList<LinkTarget> Directions { get; }

    // Прокси направлений; у приложения он один (HTTPS_PROXY)
    public Uri? Proxy { get; }

    // Для людей: без учётных данных из HTTPS_PROXY
    public string? ProxyDisplay => Proxy is { } p ? $"{p.Host}:{p.Port}" : null;

    public bool Any => Directions.Count > 0;

    private static Uri? ProxyFor(IWebProxy? proxy, Uri url)
    {
        if (proxy is null || proxy.IsBypassed(url))
            return null;

        var via = proxy.GetProxy(url);
        return via is null || via == url ? null : via;
    }

    // Адреса приложения: Bot API — если бот включён, LLM — основной провайдер и маршруты с ключом
    public static IEnumerable<(string Link, Uri Url)> Endpoints(bool telegram, IEnumerable<string> llmBaseUrls)
    {
        if (telegram)
            yield return (LinkProbe.Telegram, new Uri("https://api.telegram.org"));

        foreach (var baseUrl in llmBaseUrls)
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var url))
                yield return (url.Host, url);
    }
}
