using System.Net.Http.Headers;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

public sealed class HttpRenderClientTransport : IRenderTransport
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpRenderClientTransport(Uri serverUri, string token, string clientId, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        if (!serverUri.IsAbsoluteUri) throw new ArgumentException("The server URI must be absolute.", nameof(serverUri));
        ValidateToken(token);
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client ID is required.", nameof(clientId));

        _ownsClient = httpClient is null;
        _client = httpClient ?? new HttpClient();
        _client.BaseAddress = new Uri(serverUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("projectFrameCut-RPC/1.0");
        _client.DefaultRequestHeaders.Add("X-projectFrameCut-Client", clientId);
    }

    public async ValueTask<RenderResponseEnvelope> SendAsync(
        RenderRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] payload = RenderRpcSerializer.Serialize(request);
        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await _client.PostAsync("rpc", content, cancellationToken).ConfigureAwait(false);
        byte[] responsePayload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (responsePayload.Length == 0)
        {
            throw new HttpRequestException($"RPC server returned HTTP {(int)response.StatusCode} without a protobuf response.");
        }

        RenderResponseEnvelope envelope;
        try
        {
            envelope = RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(responsePayload);
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"RPC server returned an invalid protobuf response (HTTP {(int)response.StatusCode}).", ex);
        }

        return envelope;
    }

    public async ValueTask<byte[]> DownloadArtifactAsync(
        ArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await _client.GetAsync(
            $"artifact/{request.SessionId:D}/{request.ArtifactId:D}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Artifact download failed with HTTP {(int)response.StatusCode}.");
        return payload;
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _client.Dispose();
        return ValueTask.CompletedTask;
    }

    public static void ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 32 || token.Any(char.IsWhiteSpace))
            throw new ArgumentException("The RPC token must contain at least 32 non-whitespace characters.", nameof(token));
    }
}
