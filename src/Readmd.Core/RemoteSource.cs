using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace Readmd.Core;

/// <summary>
/// Resolves a document argument that isn't a local file: an <c>http(s)://</c> URL, or a GitHub
/// shorthand <c>gh:owner/repo[@ref][/path/to/file.md]</c> (defaults to the README on the default
/// branch). The content is written to a per-URL cache file under the temp directory so the rest of
/// the pipeline (watcher, link resolver, image loader) can treat it as a local document; relative
/// links/images in a remote document are rewritten to absolute URLs against its base.
/// </summary>
public static class RemoteSource
{
    private static readonly HttpClient Http = CreateClient();
    private const long MaxBytes = 20 * 1024 * 1024;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("readmd/1.0 (+https://github.com/MoaidHathot/readmd)");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));
        return c;
    }

    /// <summary>True for arguments readmd should fetch rather than open from disk.</summary>
    public static bool IsRemote(string arg) =>
        arg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith("gh:", StringComparison.OrdinalIgnoreCase) ||
        arg.StartsWith("github:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fetched document: its local cache path and the base URL for relative references.</summary>
    public sealed record Fetched(string LocalPath, Uri BaseUrl, Uri SourceUrl, string DisplayName);

    /// <summary>
    /// Turns the argument into a raw-content URL: GitHub blob pages become raw.githubusercontent.com,
    /// <c>gh:</c> shorthand expands to the raw README/file on the given ref.
    /// </summary>
    public static Uri ToRawUrl(string arg)
    {
        if (arg.StartsWith("gh:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var spec = arg[(arg.IndexOf(':') + 1)..].Trim('/');
            // owner/repo[@ref][/path]
            var m = Regex.Match(spec, @"^(?<owner>[^/@]+)/(?<repo>[^/@]+)(?:@(?<ref>[^/]+))?(?:/(?<path>.+))?$");
            if (!m.Success) throw new ArgumentException($"Bad GitHub shorthand '{arg}'. Use gh:owner/repo[@ref][/path/to/file.md].");
            var owner = m.Groups["owner"].Value; var repo = m.Groups["repo"].Value;
            var gitRef = m.Groups["ref"].Success ? m.Groups["ref"].Value : "HEAD";
            var path = m.Groups["path"].Success ? m.Groups["path"].Value : "README.md";
            return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{gitRef}/{path}");
        }
        var uri = new Uri(arg);
        // https://github.com/owner/repo/blob/ref/path.md → raw
        var gh = Regex.Match(uri.AbsoluteUri, @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(?:blob|raw)/(?<ref>[^/]+)/(?<path>.+)$");
        if (gh.Success)
            return new Uri($"https://raw.githubusercontent.com/{gh.Groups["owner"].Value}/{gh.Groups["repo"].Value}/{gh.Groups["ref"].Value}/{gh.Groups["path"].Value}");
        // https://github.com/owner/repo → README
        var repoOnly = Regex.Match(uri.AbsoluteUri, @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/#?]+)/?$");
        if (repoOnly.Success)
            return new Uri($"https://raw.githubusercontent.com/{repoOnly.Groups["owner"].Value}/{repoOnly.Groups["repo"].Value}/HEAD/README.md");
        // GitLab blob → raw
        var gl = Regex.Match(uri.AbsoluteUri, @"^(?<host>https?://gitlab\.com/.+?)/-/blob/(?<rest>.+)$");
        if (gl.Success) return new Uri($"{gl.Groups["host"].Value}/-/raw/{gl.Groups["rest"].Value}");
        return uri;
    }

    /// <summary>Downloads the document into the cache and rewrites relative references to absolute URLs.</summary>
    public static async Task<Fetched> FetchAsync(string arg, CancellationToken ct = default)
    {
        var raw = ToRawUrl(arg);
        using var resp = await Http.GetAsync(raw, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} fetching {raw}");
        if (resp.Content.Headers.ContentLength is { } len && len > MaxBytes)
            throw new InvalidOperationException($"Document too large ({len / 1024 / 1024} MB).");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxBytes) throw new InvalidOperationException("Document too large (limit 20 MB).");
        }
        var text = Encoding.UTF8.GetString(ms.ToArray());
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        // Base for relative links: the directory of the raw document.
        var baseUrl = new Uri(raw, ".");
        text = RewriteRelativeReferences(text, baseUrl);

        var dir = Path.Combine(Path.GetTempPath(), "readmd", "remote");
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(raw.AbsolutePath);
        if (string.IsNullOrEmpty(name) || !LinkResolver.IsMarkdownFile(name)) name = "README.md";
        var local = Path.Combine(dir, Hashing.Sha256Hex(raw.AbsoluteUri)[..12] + "-" + name);
        await File.WriteAllTextAsync(local, text, ct);
        return new Fetched(local, baseUrl, raw, DisplayUri(arg, raw));
    }

    private static string DisplayUri(string arg, Uri raw)
    {
        if (arg.StartsWith("gh:", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("github:", StringComparison.OrdinalIgnoreCase)) return arg;
        return raw.Host + raw.AbsolutePath;
    }

    // Matches Markdown link/image destinations and HTML src/href attributes with relative targets.
    private static readonly Regex MdLink = new(@"(?<prefix>!?\[[^\]]*\]\()(?<url>[^)\s]+)(?<suffix>(?:\s+""[^""]*"")?\))", RegexOptions.Compiled);
    private static readonly Regex HtmlRef = new(@"(?<prefix>\b(?:src|href|srcset)\s*=\s*"")(?<url>[^""]+)(?<suffix>"")", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RefDef = new(@"(?m)^(?<prefix>\s{0,3}\[[^\]]+\]:\s+)(?<url>\S+)", RegexOptions.Compiled);

    /// <summary>Rewrites relative URLs in links, images, HTML attributes and reference definitions to absolute ones.</summary>
    public static string RewriteRelativeReferences(string markdown, Uri baseUrl)
    {
        string Fix(Match m)
        {
            var url = m.Groups["url"].Value;
            return m.Groups["prefix"].Value + Absolutize(url, baseUrl) + m.Groups["suffix"].Value;
        }
        // Don't touch fenced code blocks.
        var sb = new StringBuilder(markdown.Length + 256);
        bool inFence = false;
        string? fenceMarker = null;
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                var marker = trimmed[..3];
                if (!inFence) { inFence = true; fenceMarker = marker; }
                else if (fenceMarker == marker) { inFence = false; fenceMarker = null; }
                sb.Append(line).Append('\n');
                continue;
            }
            if (!inFence)
            {
                line = MdLink.Replace(line, Fix);
                line = HtmlRef.Replace(line, Fix);
                line = RefDef.Replace(line, Fix);
            }
            sb.Append(line).Append('\n');
        }
        return sb.Length > 0 ? sb.ToString(0, sb.Length - 1) : "";
    }

    private static string Absolutize(string url, Uri baseUrl)
    {
        if (string.IsNullOrEmpty(url) || url.StartsWith('#') || url.Contains("://") || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("//") || url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
            return url;
        try
        {
            // srcset may carry "url 2x" descriptors.
            int sp = url.IndexOf(' ');
            var target = sp > 0 ? url[..sp] : url;
            var rest = sp > 0 ? url[sp..] : "";
            return new Uri(baseUrl, target).AbsoluteUri + rest;
        }
        catch { return url; }
    }
}
