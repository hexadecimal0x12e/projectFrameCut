using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.Services;

internal static class PluginIsolationHostHandshake
{
    public static async Task AuthorizeRuntimeAsync(Stream stream, PluginIsolationLaunchContext context, CancellationToken cancellationToken)
    {
        var data = await IsolationFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("The isolation runtime closed the pipe before authorization.");
        var request = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(data);
        if (request.Operation != RenderOperation.IsolationAuthorizePlugin)
            throw new UnauthorizedAccessException("The isolation runtime did not send a plugin authorization request.");

        var authorization = RenderRpcSerializer.Deserialize<IsolationPluginAuthorizationRequest>(request.Payload);
        var suppliedToken = Encoding.UTF8.GetBytes(authorization.AuthenticationToken);
        var expectedToken = Encoding.UTF8.GetBytes(context.AuthenticationToken);
        var valid = request.ProtocolVersion == RenderProtocol.CurrentVersion
            && authorization.ProtocolVersion == PluginIsolationProtocol.CurrentVersion
            && string.Equals(authorization.PluginId, context.PluginId, StringComparison.Ordinal)
            && string.Equals(authorization.InstancePackageName, context.InstancePackageName, StringComparison.Ordinal)
            && authorization.Challenge.Length >= 16
            && suppliedToken.Length == expectedToken.Length
            && CryptographicOperations.FixedTimeEquals(suppliedToken, expectedToken);

        var encryptedAssemblyPath = Path.Combine(context.PluginRoot, context.PluginId + ".dll.enc");
        valid &= File.Exists(encryptedAssemblyPath);
        if (valid)
        {
            var encryptedAssembly = await File.ReadAllBytesAsync(encryptedAssemblyPath, cancellationToken).ConfigureAwait(false);
            valid = string.Equals(
                authorization.EncryptedAssemblyHash,
                Convert.ToHexString(SHA256.HashData(encryptedAssembly)).ToLowerInvariant(),
                StringComparison.OrdinalIgnoreCase);
        }

        RenderResponseEnvelope response = valid
            ? new()
            {
                RequestId = request.RequestId,
                Payload = RenderRpcSerializer.Serialize(new IsolationPluginAuthorizationResponse
                {
                    PluginId = context.PluginId,
                    Challenge = authorization.Challenge,
                    DecryptionKey = context.PluginEncryptionKey,
                }),
            }
            : new()
            {
                RequestId = request.RequestId,
                Error = new()
                {
                    Code = RenderErrorCode.Unauthorized,
                    Message = "Plugin authorization was rejected.",
                    Details = "The isolation runtime authorization data did not match the installed plugin.",
                },
            };
        if (valid) projectFrameCut.Shared.Logger.Log($"Authorized isolated plugin '{context.PluginId}' for instance '{context.InstancePackageName}'.");
        await IsolationFrame.WriteAsync(stream, RenderRpcSerializer.Serialize(response), cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) response.Error.ThrowAsException();
    }
}
