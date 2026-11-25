using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HhAutoApplier.Domain.Dialogs;

public enum EmployerLinkKind { Form, Test }

public enum EmployerLinkState { Pending, Completed, Dismissed }

public sealed partial class EmployerLink
{
    private static readonly string[] FormHosts = ["forms.gle", "forms.yandex.ru", "forms.office.com", "typeform.com", "anketolog.ru", "testograf.ru"];
    private static readonly string[] TestHosts = ["hiringscore.ai", "hackerrank.com", "codility.com", "testgorilla.com", "skillaz.co", "skillaz.ru"];

    public string Key { get; private set; } = "";
    public string Url { get; private set; } = "";
    public string Domain { get; private set; } = "";
    public EmployerLinkKind Kind { get; private set; }
    public EmployerLinkState State { get; private set; }
    public long MessageId { get; private set; }
    public string Quote { get; private set; } = "";
    public string? Action { get; private set; }
    public bool? RequiresAction { get; private set; }
    public DateTimeOffset DetectedAt { get; private set; }
    public DateTimeOffset? DueAt { get; private set; }
    public DateTimeOffset? RemindAt { get; private set; }
    public DateTimeOffset? RemindedAt { get; private set; }
    public DateTimeOffset? NotifiedAt { get; private set; }

    public static IEnumerable<EmployerLink> Extract(string text, long messageId, DateTimeOffset now)
    {
        foreach (Match match in LinkPattern().Matches(text))
        {
            var raw = match.Value.TrimEnd('.', ',', ';', '!', '?', ')', ']', '}', '»');
            var qualified = raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(qualified ? raw : $"https://{raw}", UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
                || uri.AbsoluteUri.Length > 1500)
                continue;

            var host = uri.IdnHost;
            var kind = host == "docs.google.com" && uri.AbsolutePath.StartsWith("/forms/", StringComparison.Ordinal)
                || FormHosts.Any(domain => MatchesHost(host, domain)) ? EmployerLinkKind.Form
                : TestHosts.Any(domain => MatchesHost(host, domain)) ? EmployerLinkKind.Test : (EmployerLinkKind?)null;
            if (kind is null)
                continue;

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
            yield return new EmployerLink
            {
                Key = Convert.ToHexStringLower(hash.AsSpan(0, 8)), Url = uri.AbsoluteUri, Domain = host,
                Kind = kind.Value, MessageId = messageId, Quote = text[..Math.Min(400, text.Length)],
                DetectedAt = now, RemindAt = now.AddDays(1),
            };
        }
    }

    public void ApplyAnalysis(bool requiresAction, string? action, DateTimeOffset? dueAt, DateTimeOffset now)
    {
        RequiresAction = requiresAction;
        Action = string.IsNullOrWhiteSpace(action) ? null : action.Trim()[..Math.Min(200, action.Trim().Length)];
        DueAt = dueAt?.ToUniversalTime();
        if (!requiresAction || RemindedAt.HasValue || State != EmployerLinkState.Pending)
            return;
        RemindAt = DueAt is { } due ? (due > now.AddDays(1) ? due.AddDays(-1) : null) : (RemindAt ?? now.AddDays(1));
    }

    public void Resolve(EmployerLinkState state)
    {
        State = state;
        RemindAt = null;
    }

    public void RemindTomorrow(DateTimeOffset now)
    {
        if (State == EmployerLinkState.Pending && RemindedAt is null)
            RemindAt = now.AddDays(1);
    }

    public void MarkNotified(DateTimeOffset now) => NotifiedAt ??= now;

    public void MarkReminded(DateTimeOffset now) => (RemindedAt, RemindAt) = (now, null);

    private static bool MatchesHost(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    [GeneratedRegex("""(?<![\w@./:%-])(?:https?://)?(?:[a-z0-9][a-z0-9-]*\.)+[a-z]{2,}(?:[:/?#][^\s<>"'«»]*)?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex LinkPattern();
}
