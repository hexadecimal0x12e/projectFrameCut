using System.Security.Cryptography;
using System.Text.Json;
using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

public sealed class ExternalRpcRequest
{
    public int Version { get; set; } = 1;
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public string AppName { get; set; } = "";
    public string Author { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);
    public string Status { get; set; } = "pending";
    public string EncryptedConnection { get; set; } = "";

    public void Validate()
    {
        if (Version != 1 || RequestId == Guid.Empty || Status != "pending" || EncryptedConnection != "")
            throw new ArgumentException("Invalid RPC request state.");
        foreach (var s in new[] { AppName, Author, Purpose })
            if (string.IsNullOrWhiteSpace(s) || s.Length > 2048 || s.Any(c => char.IsControl(c) && c != '\n' && c != '\r'))
                throw new ArgumentException("Invalid RPC application description.");
        using var rsa = OpenPublicKey();
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
    public string PipeName { get; set; } = "";
    public string Token { get; set; } = "";
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
