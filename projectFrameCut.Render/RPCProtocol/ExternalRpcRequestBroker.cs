using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

internal sealed class ExternalRpcRequestBroker : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _authorizationPath;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Task _poll;
    private FileStream? _file;
    private ExternalRpcRequest? _request;
    private Guid _claim;
    private DateTimeOffset _frontendUntil;

    public ExternalRpcRequestBroker(string directory, CancellationToken ct)
    {
        _directory = Path.GetFullPath(directory);
        _authorizationPath = ExternalRpcAuthorizationStore.GetPath(_directory);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Directory.CreateDirectory(_directory);
        _poll = PollAsync();
    }

    public async Task<PendingExternalRpcRequest> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _frontendUntil = DateTimeOffset.UtcNow.AddSeconds(5);
            return _request is null ? new() : new() { ClaimId = _claim, Json = JsonSerializer.Serialize(_request) };
        }
        finally { _gate.Release(); }
    }

    private async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (_request is not null && _request.ExpiresAt <= DateTimeOffset.UtcNow)
                    {
                        WriteResult("expired");
                        Release();
                    }
                    if (_request is not null || DateTimeOffset.UtcNow > _frontendUntil) continue;
                    foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
                    {
                        FileStream? file = null;
                        try
                        {
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                            // Retain the handle through authorization: other backends and writers cannot replace this request.
                            file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                            if (file.Length > 32768) continue;
                            var request = ExternalRpcRequest.Read(file);
                            if (request.Status != "pending") continue;
                            request.Validate();
                            _file = file;
                            file = null;
                            _request = request;
                            _claim = Guid.NewGuid();
                            if (request.ExpiresAt <= DateTimeOffset.UtcNow || request.ExpiresAt > DateTimeOffset.UtcNow.AddHours(1))
                            {
                                WriteResult("expired");
                                Release();
                                continue;
                            }
                            Log($"External RPC request claimed: {request.RequestId}.");
                            break;
                        }
                        catch (IOException) { }
                        catch (Exception ex) when (ex is JsonException or ArgumentException or CryptographicException or FormatException) { }
                        finally { file?.Dispose(); }
                    }
                }
                catch (Exception ex) { Release(); Log(ex, "Poll external RPC requests"); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task ResolveAsync(ResolveExternalRpcRequest decision, Func<Guid, string, Task<string>> createPipe, Action<string> revokePipe, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? token = null;
        try
        {
            if (_request is null || decision.ClaimId != _claim) throw new ArgumentException("RPC authorization is no longer pending.");
            if (_request.ExpiresAt <= DateTimeOffset.UtcNow) { WriteResult("expired"); return; }
            if (!decision.Approved) { WriteResult("denied"); return; }
            ExternalRpcClientAuthorization? authorization = null;
            if (_request.IsPersistent)
            {
                authorization = ExternalRpcAuthorizationStore.Find(_authorizationPath, _request.ClientId)
                    ?? throw new UnauthorizedAccessException("Persistent RPC client is not authorized.");
                if (authorization.Revoked || authorization.PublicKey != _request.PublicKey)
                    throw new UnauthorizedAccessException("Persistent RPC client is not authorized.");
                if (!_request.VerifySignature()) throw new UnauthorizedAccessException("Persistent RPC request signature is invalid.");
            }
            using var rsa = _request.OpenPublicKey();
            token = await createPipe(authorization?.ClientId ?? Guid.Empty, authorization?.AppName ?? _request.AppName).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _lifetime.Token.ThrowIfCancellationRequested();
            _request.EncryptedConnection = Convert.ToBase64String(rsa.Encrypt(JsonSerializer.SerializeToUtf8Bytes(new ExternalRpcConnection
            {
                RequestId = _request.RequestId,
                ClientId = _request.ClientId,
                ServiceId = _request.ServiceId,
                PipeName = RenderProtocol.AdditionalPipePrefix + token,
                Token = token,
            }), RSAEncryptionPadding.OaepSHA256));
            if (authorization is not null && _request.LaunchClient)
            {
                if (string.IsNullOrWhiteSpace(authorization.ExecutablePath) || !File.Exists(authorization.ExecutablePath))
                    throw new FileNotFoundException("Authorized external RPC client executable was not found.", authorization.ExecutablePath);
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = authorization.ExecutablePath,
                    Arguments = authorization.LaunchArguments ?? "",
                    UseShellExecute = true,
                }) ?? throw new InvalidOperationException("Unable to start the authorized external RPC client.");
                Log($"Started persistent external RPC client {authorization.ClientId} for service '{_request.ServiceId}'.");
            }
            WriteResult("approved");
            if (authorization is not null) ExternalRpcAuthorizationStore.MarkUsed(_authorizationPath, authorization.ClientId);
        }
        catch
        {
            if (token is not null) revokePipe(token);
            if (_request is not null && decision.ClaimId == _claim)
            {
                _request.EncryptedConnection = "";
                try { WriteResult("failed"); } catch { }
            }
            throw;
        }
        finally
        {
            if (decision.ClaimId == _claim) Release();
            _gate.Release();
        }
    }

    private void WriteResult(string status)
    {
        _request!.Status = status;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_request);
        _file!.Position = 0;
        _file.Write(bytes);
        _file.SetLength(bytes.Length);
        _file.Flush(true);
        Log($"External RPC request {_request.RequestId}: {status}.");
    }

    private void Release()
    {
        _file?.Dispose();
        _file = null;
        _request = null;
        _claim = Guid.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _poll.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Release(); }
        finally { _gate.Release(); }
        _lifetime.Dispose();
    }
}
