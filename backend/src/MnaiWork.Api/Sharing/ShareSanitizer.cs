using System.Text.RegularExpressions;

namespace MnaiWork.Api.Sharing;

public static class ShareSanitizer
{
    private static string Replace(string text, string pattern, string replacement) => Regex.Replace(
        text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    public static string Clean(string text)
    {
        if (text.Length > 32_000) throw new InvalidDataException("A message exceeds the sharing size limit.");
        text = Replace(text, @"-----BEGIN [^-]*(?:PRIVATE KEY|CERTIFICATE)-----[\s\S]*?-----END [^-]+-----", "[redacted credential]");
        text = Replace(text, "[\"']?(?:[\\w-]*(?:secret|password|passwd|token|apikey|api_key|accountkey|connectionstring|client_secret)[\\w-]*)[\"']?\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;}]+)", "[redacted credential]");
        text = Replace(text, @"\bBearer\s+[^\s,;]+|\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[redacted credential]");
        text = Replace(text, @"(?:https?://|data:|blob:|file:|vscode:|www\.)[^\s<>\""')]+", "[link removed]");
        text = Replace(text, @"!?\[([^\]\r\n]*)\]\([^\r\n]*?\)", "$1 [link removed]");
        text = Replace(text, @"(?m)^\s*\[[^\]]+\]:[^\r\n]*$", "[link removed]");
        text = Replace(text, @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", "[email removed]");
        text = Replace(text, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\b[0-9a-f]{32,}\b", "[identifier removed]");
        text = Replace(text, @"/(?:subscriptions|resourceGroups)/[^\s\""<>]+", "[resource identifier removed]");
        text = Replace(text, @"<[^>]*>", "");
        return text.Trim();
    }
}