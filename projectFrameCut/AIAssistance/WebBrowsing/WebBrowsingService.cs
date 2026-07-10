namespace projectFrameCut.AIAssistance;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls;
using projectFrameCut.Shared;

/// <summary>
/// Loads a web page in a MAUI WebView and returns its rendered text.
/// </summary>
public sealed class WebBrowsingService : IDisposable
{
    private const int DefaultMaximumCharacters = 30_000;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, byte> AuthorizedDomains = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CurrentGate = new();
    private static WebBrowsingService? _current;

    private readonly WebView _webView;
    private readonly Func<Uri, Task<bool>> _authorizeDomain;
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

    public WebBrowsingService(WebView webView, Func<Uri, Task<bool>> authorizeDomain)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _authorizeDomain = authorizeDomain ?? throw new ArgumentNullException(nameof(authorizeDomain));
        Current = this;
    }

    public async Task<string> BrowseAsync(string url, int maximumCharacters = DefaultMaximumCharacters)
    {
        ThrowIfDisposed();

        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return "Error: only absolute http:// or https:// URLs are supported.";
        }

        if (IsUnsafeHost(uri.Host))
            return "Error: local, loopback, and link-local hosts are not allowed.";

        maximumCharacters = Math.Clamp(maximumCharacters, 1_000, DefaultMaximumCharacters);
        string domain = NormalizeDomain(uri.Host);
        if (!AuthorizedDomains.ContainsKey(domain))
        {
            if (!await _authorizeDomain(uri).ConfigureAwait(false))
                return $"Access to {domain} was denied by the user.";

            AuthorizedDomains.TryAdd(domain, 0);
        }

        await _navigationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await NavigateAndExtractAsync(uri, maximumCharacters).ConfigureAwait(false);
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

    private async Task<string> NavigateAndExtractAsync(Uri uri, int maximumCharacters)
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

            string script = $$"""
                (() => {
                    const root = document.body || document.documentElement;
                    const text = root ? (root.innerText || root.textContent || '') : '';
                    return JSON.stringify({
                        title: document.title || '',
                        url: location.href,
                        text: text
                    });
                })()
                """;
            string? raw = await MainThread.InvokeOnMainThreadAsync(() => _webView.EvaluateJavaScriptAsync(script)).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
                return "Error: the webpage did not contain readable text.";

            WebPageResult? result = JsonSerializer.Deserialize<WebPageResult>(JsonSerializer.Deserialize<string[]>($"[\"{raw}\"]")?[0] ?? raw.Replace("\\\"", "\""));
            if (result is null || string.IsNullOrWhiteSpace(result.Text))
                return "Error: the webpage did not contain readable text.";

            string text = result.Text.Trim();
            if (text.Length > maximumCharacters)
                text = text[..maximumCharacters] + "\n[Page text truncated]";

            return $"URL: {result.Url}\nTitle: {result.Title ?? "(untitled)"}\n\n{text}";
        }
        catch (Exception ex)
        {
            Log(ex, $"browse {uri}", this);
            throw;
        }
        finally
        {
            _webView.Navigated -= handler;
        }
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

    private sealed class WebPageResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
        [JsonPropertyName("url")]
        public string? Url { get; set; }
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
