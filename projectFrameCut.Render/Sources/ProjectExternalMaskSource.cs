using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Render.Sources;

public sealed class ProjectExternalMaskSource : RemoteRpcMaskSource
{
    private readonly ProjectExternalSourceHost host;

    public ProjectExternalMaskSource(Guid importId, ExternalMaskSourceDescriptor descriptor)
        : this(Parse(CreatePath(importId, descriptor)), ProjectExternalSourceRuntime.Current) { }

    private ProjectExternalMaskSource(ExternalMaskSourceReference source, ProjectExternalSourceHost host)
        : base(source, $"#{nameof(ProjectExternalMaskSource)}:{source.Encode()}") => this.host = host;

    public static string CreatePath(Guid importId, ExternalMaskSourceDescriptor descriptor)
    {
        var source = CreateReference(descriptor);
        source.ClientId = source.Descriptor.ClientId = importId;
        return $"#{nameof(ProjectExternalMaskSource)}:{source.Encode()}";
    }

    public new static bool IsPath(string? path) => path?.StartsWith($"#{nameof(ProjectExternalMaskSource)}:", StringComparison.Ordinal) == true;
    private static ExternalMaskSourceReference Parse(string path)
    {
        if (!IsPath(path)) throw new ArgumentException("Invalid project external mask source path.", nameof(path));
        return ExternalMaskSourceReference.Decode(path[($"#{nameof(ProjectExternalMaskSource)}:").Length..]);
    }

    public new static ProjectExternalMaskSource Open(string path) => Open(path, ProjectExternalSourceRuntime.Current);
    public static ProjectExternalMaskSource Open(string path, ProjectExternalSourceHost host) => new(Parse(path), host);
    protected override ValueTask<ExternalVideoFrame> ProcessAsync(ExternalMaskSourceProcessRequest request, CancellationToken token) =>
        host.GetClient(Source.ClientId).ProcessMaskAsync(request, token);
}
