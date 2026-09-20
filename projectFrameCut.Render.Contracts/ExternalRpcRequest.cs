using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

public sealed class ExternalRpcRequest
{
    public int Version { get; set; } = 1;
    public string Mode { get; set; } = "one-time";
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public string ServiceId { get; set; } = "";
    public bool LaunchClient { get; set; }
    public string AppName { get; set; } = "";
    public string Author { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);
    public string Status { get; set; } = "pending";
    public string EncryptedConnection { get; set; } = "";
    public string Signature { get; set; } = "";

    public bool IsPersistent => Mode == "persistent";

    public void Validate()
    {
        if ((Version != 1 && Version != 2) || RequestId == Guid.Empty || Status != "pending" || EncryptedConnection != "")
            throw new ArgumentException("Invalid RPC request state.");
        if (IsPersistent)
        {
            if (Version != 2 || ClientId == Guid.Empty || string.IsNullOrWhiteSpace(ServiceId) || ServiceId.Length > 24 || string.IsNullOrWhiteSpace(Signature) || Signature.Length > 16384)
                throw new ArgumentException("Invalid persistent RPC request.");
        }
        else
        {
            if (Mode != "one-time" || Version != 1 || ClientId != Guid.Empty || ServiceId != "" || LaunchClient || Signature != "")
                throw new ArgumentException("Invalid one-time RPC request.");
            foreach (var s in new[] { AppName, Author, Purpose })
                if (string.IsNullOrWhiteSpace(s) || s.Length > 2048 || s.Any(c => char.IsControl(c) && c != '\n' && c != '\r'))
                    throw new ArgumentException("Invalid RPC application description.");
        }
        using var rsa = OpenPublicKey();
    }

    public byte[] GetSignaturePayload() => Encoding.UTF8.GetBytes(string.Join('\n',
        Version.ToString(System.Globalization.CultureInfo.InvariantCulture), Mode, RequestId.ToString("D"), ClientId.ToString("D"),
        ServiceId, LaunchClient ? "1" : "0", PublicKey,
        ExpiresAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

    public void Sign(RSA rsa) => Signature = Convert.ToBase64String(rsa.SignData(GetSignaturePayload(), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));

    public bool VerifySignature()
    {
        try
        {
            using var rsa = OpenPublicKey();
            return rsa.VerifyData(GetSignaturePayload(), Convert.FromBase64String(Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    public RSA OpenPublicKey()
    {
        var rsa = RSA.Create();
        try
        {
            var bytes = Convert.FromBase64String(PublicKey);
            rsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || rsa.KeySize < 3072 || rsa.KeySize > 8192)
                throw new ArgumentException("Expected a 3072-8192 bit RSA SubjectPublicKeyInfo public key.");
            return rsa;
        }
        catch { rsa.Dispose(); throw; }
    }

    public static ExternalRpcRequest Read(Stream stream) =>
        JsonSerializer.Deserialize<ExternalRpcRequest>(stream) ?? throw new ArgumentException("Empty RPC request.");

    public static ExternalRpcRequest ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > 32768) throw new ArgumentException("RPC request exceeds 32 KiB.");
        return Read(stream);
    }
}

public sealed class ExternalRpcConnection
{
    public Guid RequestId { get; set; }
    public Guid ClientId { get; set; }
    public string ServiceId { get; set; } = "";
    public string PipeName { get; set; } = "";
}

[ProtoContract]
public sealed class ExternalRpcClientAuthorization
{
    [ProtoMember(1)]
    public Guid ClientId { get; set; } = Guid.NewGuid();
    [ProtoMember(2)]
    public string AppName { get; set; } = "";
    [ProtoMember(3)]
    public string Author { get; set; } = "";
    [ProtoMember(4)]
    public string Purpose { get; set; } = "";
    [ProtoMember(5)]
    public string PublicKey { get; set; } = "";
    [ProtoMember(6)]
    public string PublicKeyFingerprint { get; set; } = "";
    [ProtoMember(7)]
    public string? ExecutablePath { get; set; }
    [ProtoMember(8)]
    public string? LaunchArguments { get; set; }
    [ProtoMember(9)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [ProtoMember(10)]
    public DateTimeOffset? LastUsedAt { get; set; }
    [ProtoMember(11)]
    public bool Revoked { get; set; }
}

[ProtoContract]
public sealed class PendingExternalRpcRequest
{
    [ProtoMember(1)] public string Json { get; set; } = "";
    [ProtoMember(2)] public Guid ClaimId { get; set; }
}

[ProtoContract]
public sealed class ResolveExternalRpcRequest
{
    [ProtoMember(1)] public Guid ClaimId { get; set; }
    [ProtoMember(2)] public bool Approved { get; set; }
    [ProtoMember(3)] public Guid GuiSessionId { get; set; }
}
