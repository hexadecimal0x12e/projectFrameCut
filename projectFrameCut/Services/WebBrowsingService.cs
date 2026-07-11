namespace projectFrameCut.AIAssistance;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls;
using projectFrameCut.Shared;

/// <summary>
/// Loads a web page in a MAUI WebView and returns its rendered text,
/// along with extracted hyperlinks and image URLs for AI consumption.
/// </summary>
public sealed class WebBrowsingService : IDisposable
{
    private const int DefaultMaximumCharacters = 30_000;
    private const int MaxLinks = 100;
    private const int MaxImages = 50;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, bool> AuthorizedDomains = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CurrentGate = new();
    private static WebBrowsingService? _current;

    private readonly WebView _webView;
    private readonly Func<Uri, Task<(bool allow, bool remember)>> _authorizeDomain;
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private bool _disposed;

    public static WebBrowsingService? Current
    {
        get
        {
            lock (CurrentGate)
                return _current;
        }
        private set
        {
            lock (CurrentGate)
                _current = value;
        }
    }

    public WebBrowsingService(WebView webView, Func<Uri, Task<(bool allow, bool remember)>> authorizeDomain)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _authorizeDomain = authorizeDomain ?? throw new ArgumentNullException(nameof(authorizeDomain));
        Current = this;
    }

    /// <summary>
    /// Browses a web page and returns a formatted markdown string containing
    /// the page title, text content, extracted hyperlinks, and image URLs.
    /// </summary>
    public async Task<string> BrowseAsync(string url, int maximumCharacters = DefaultMaximumCharacters)
    {
        ThrowIfDisposed();

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("Only absolute http:// or https:// URLs are supported.");
        }

        if (IsUnsafeHost(uri.Host))
            throw new InvalidOperationException("Local, loopback, and link-local hosts are not allowed.");

        maximumCharacters = Math.Clamp(maximumCharacters, 1_000, DefaultMaximumCharacters);
        string domain = NormalizeDomain(uri.Host);
        if (!AuthorizedDomains.TryGetValue(domain, out var rememberAllow))
        {
            var (allow, remember) = await _authorizeDomain(uri).ConfigureAwait(false);
            if (!allow)
            {
                if (remember)
                    AuthorizedDomains.TryAdd(domain, false);
                return $"Access to {domain} was denied by the user.";
            }
            if (remember)
                AuthorizedDomains.TryAdd(domain, true);
        }
        else if (!rememberAllow)
        {
            return $"Access to {domain} was previously denied by the user.";
        }

        await _navigationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            WebPageContent? result = await NavigateAndExtractAsync(uri, maximumCharacters).ConfigureAwait(false);
            return result?.ToMarkdown() ?? "Error: the webpage did not contain readable text.";
        }
        catch (OperationCanceledException)
        {
            return "Error: the webpage took too long to load.";
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Browse webpage", typeof(WebBrowsingService));
            return $"Error: unable to browse the webpage: {ex.Message}";
        }
        finally
        {
            _navigationLock.Release();
        }
    }

    /// <summary>
    /// Browses a web page and returns a structured <see cref="WebPageContent"/> object
    /// containing the page title, text, parsed links, and image URLs.
    /// Returns null on failure or denied access.
    /// </summary>
    public async Task<WebPageContent?> BrowseStructuredAsync(string url, int maximumCharacters = DefaultMaximumCharacters)
    {
        ThrowIfDisposed();

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("Only absolute http:// or https:// URLs are supported.");
        }

        if (IsUnsafeHost(uri.Host))
            throw new InvalidOperationException("Local, loopback, and link-local hosts are not allowed.");

        maximumCharacters = Math.Clamp(maximumCharacters, 1_000, DefaultMaximumCharacters);
        string domain = NormalizeDomain(uri.Host);
        if (!AuthorizedDomains.TryGetValue(domain, out var rememberAllow))
        {
            var (allow, remember) = await _authorizeDomain(uri).ConfigureAwait(false);
            if (!allow)
            {
                if (remember)
                    AuthorizedDomains.TryAdd(domain, false);
                return null;
            }
            if (remember)
                AuthorizedDomains.TryAdd(domain, true);
        }
        else if (!rememberAllow)
        {
            return null;
        }

        await _navigationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await NavigateAndExtractAsync(uri, maximumCharacters).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Browse webpage", typeof(WebBrowsingService));
            return null;
        }
        finally
        {
            _navigationLock.Release();
        }
    }

    private async Task<WebPageContent?> NavigateAndExtractAsync(Uri uri, int maximumCharacters)
    {
        var navigation = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<WebNavigatedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (args.Result == WebNavigationResult.Success)
                navigation.TrySetResult(null);
            else
                navigation.TrySetException(new InvalidOperationException($"navigation failed: {args.Result}"));
        };

        _webView.Navigated += handler;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => _webView.Source = uri.ToString()).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(NavigationTimeout);
            await navigation.Task.WaitAsync(timeout.Token).ConfigureAwait(false);

            string script = GetExtractionScript();
            string? raw = await MainThread.InvokeOnMainThreadAsync(() => _webView.EvaluateJavaScriptAsync(script)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            WebPageContent? result = DeserializeResult(raw);
            if (result is null || string.IsNullOrWhiteSpace(result.Text))
                return null;

            // Apply text length limit
            result.Text = result.Text.Trim();
            if (result.Text.Length > maximumCharacters)
                result.Text = result.Text[..maximumCharacters] + "\n[Page text truncated]";

            return result;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, $"browse {uri}", typeof(WebBrowsingService));
            throw;
        }
        finally
        {
            _webView.Navigated -= handler;
        }
    }

    private static string GetExtractionScript()
    {
        return $$"""
            (() => {
                const root = document.body || document.documentElement;
                const text = root ? (root.innerText || root.textContent || '') : '';

                const links = Array.from(document.querySelectorAll('a[href]'))
                    .filter(a => { try { return new URL(a.href).protocol.startsWith('http'); } catch { return false; {{'}'}}})
                    .map(a => ({
                        url: a.href,
                        text: (a.innerText || a.textContent || '').trim().slice(0, 200)
                    }))
                    .slice(0, {{MaxLinks}});

                const images = Array.from(document.querySelectorAll('img[src]'))
                    .filter(img => { try { return new URL(img.src).protocol.startsWith('http'); } catch { return false; {{'}'}}})
                    .map(img => ({
                        url: img.src,
                        alt: (img.alt || '').trim().slice(0, 200)
                    }))
                    .slice(0, {{MaxImages}});

                return JSON.stringify({
                    title: document.title || '',
                    url: location.href,
                    text: text,
                    links: links,
                    images: images
                });
            })()
            """;
    }

    /// <summary>
    /// Safely deserializes the raw JavaScript evaluation result into <see cref="WebPageContent"/>.
    /// Handles platform-specific escaping differences in the WebView bridge.
    /// </summary>
    private static WebPageContent? DeserializeResult(string raw)
    {
        try
        {
            // Wrap the raw value in an array to safely extract an unescaped inner string
            string? inner = JsonSerializer.Deserialize<string[]>($"[\"{raw}\"]")?[0] ?? JsonSerializer.Deserialize<string>(raw.Replace("\\\"", "\""));
            if (inner is not null)
                return JsonSerializer.Deserialize<WebPageContent>(inner);
        }
        catch (Exception ex)
        {
            Log(ex, $"Deserialization failed for raw input: \r\n{raw}");
            throw;
        }

        return null;
    }

    private static string NormalizeDomain(string host) =>
        host.TrimEnd('.').ToLowerInvariant();

    private static bool IsUnsafeHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out IPAddress? address))
            return false;

        return IPAddress.IsLoopback(address)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.GetAddressBytes()[0] == 10
            || (address.GetAddressBytes()[0] == 192 && address.GetAddressBytes()[1] == 168)
            || (address.GetAddressBytes()[0] == 172 && address.GetAddressBytes()[1] >= 16 && address.GetAddressBytes()[1] <= 31);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (ReferenceEquals(Current, this))
            Current = null;
        _navigationLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WebBrowsingService));
    }
}

/// <summary>
/// Structured content extracted from a web page, including plain text, hyperlinks, and image URLs.
/// </summary>
public sealed class WebPageContent
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("links")]
    public List<PageLink>? Links { get; set; }

    [JsonPropertyName("images")]
    public List<PageImage>? Images { get; set; }

    /// <summary>
    /// Formats the extracted content as a markdown string suitable for AI consumption.
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();

        sb.AppendLine($"URL: {Url ?? "(unknown)"}");
        sb.AppendLine($"Title: {Title ?? "(untitled)"}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(Text))
            sb.AppendLine(Text);

        if (Links is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine($"**Links ({Links.Count} found):**");
            foreach (var link in Links.Take(20))
            {
                string label = string.IsNullOrWhiteSpace(link.Text) ? link.Url : link.Text;
                sb.AppendLine($"- [{label.Replace("\n", " ").Replace("\r", "")}]({link.Url})");
            }
            if (Links.Count > 20)
                sb.AppendLine($"- ... and {Links.Count - 20} more links");
        }

        if (Images is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine($"**Images ({Images.Count} found):**");
            foreach (var image in Images.Take(10))
            {
                string desc = string.IsNullOrWhiteSpace(image.Alt)
                    ? image.Url
                    : $"{image.Alt.Replace("\n", " ").Replace("\r", "")} — {image.Url}";
                sb.AppendLine($"- ![thumbnail]({image.Url}){desc}");
            }
            if (Images.Count > 10)
                sb.AppendLine($"- ... and {Images.Count - 10} more images");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Represents a hyperlink extracted from a web page.
/// </summary>
public sealed class PageLink
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Text) ? Url : $"{Text} ({Url})";
}

/// <summary>
/// Represents an image extracted from a web page.
/// </summary>
public sealed class PageImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("alt")]
    public string? Alt { get; set; }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Alt) ? Url : $"{Alt} ({Url})";
}
