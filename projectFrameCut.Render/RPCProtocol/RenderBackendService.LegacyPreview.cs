using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Rendering;
using System.Buffers.Binary;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private RenderArtifact RenderLegacyClipPreview(BackendSession session, ClipPreviewRequest request, FrameHashResult identity,
        int projectWidth, int projectHeight, int canvasWidth, int canvasHeight, Func<IPicture> render, Func<bool> canPersist,
        bool registerArtifact, CancellationToken token)
    {
        string format = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb ? "vfd16" : "vfd8";
        string prefix = (request.BeforeLayout ? "before-layout_" : "")
            + (string.IsNullOrEmpty(session.CacheNamespace) ? "" : session.CacheNamespace + "_");
        bool persistent = identity.CanReuse;
        string temporaryEntry = $"thumbs/unCacheablePerClip/{session.Id:N}/clip_{request.ClipId:N}_{Guid.NewGuid():N}.vfd";
        string relativePath = persistent
            ? $"thumbs/perClip/{request.ClipId}/dynamic/dynamic_{ClipPreviewCacheVersion}_{prefix}{identity.Hash}_{projectWidth}x{projectHeight}_{canvasWidth}x{canvasHeight}_{format}.vfd"
            : temporaryEntry;
        string path = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
        bool hit = persistent && File.Exists(path);
        if (!hit)
        {
            using var picture = render();
            token.ThrowIfCancellationRequested();
            if (persistent && !canPersist())
            {
                persistent = false;
                relativePath = temporaryEntry;
                path = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
            }
            string temporaryPath = _artifacts.CreateTemporaryPath(path);
            try
            {
                picture.SaveToDisk(temporaryPath, PreviewVfdEncoder);
                token.ThrowIfCancellationRequested();
                _artifacts.CommitTemporaryFile(temporaryPath, path);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }
        using var stream = PreviewFrameContainer.Open(path);
        Span<byte> header = stackalloc byte[18];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("VFCD"u8)) throw new InvalidDataException("Invalid VFD preview header.");
        int offset = header[4] == 0xFF ? 10 : 5;
        int width = BinaryPrimitives.ReadInt32LittleEndian(header[offset..]), height = BinaryPrimitives.ReadInt32LittleEndian(header[(offset + 4)..]);
        if (persistent) TrackPreviewCacheAccess(session.ProjectRoot, relativePath, request.ClipId, request.FrameIndex, $"{width}x{height}:{format}");
        if (request.PreferredPixelFormat == PreviewPixelFormat.PngImage)
            return MaterializePreviewPng(session, relativePath, request.ClipId, request.FrameIndex, width, height, hit, persistent, token);
        return registerArtifact
            ? _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "application/x-projectframecut-vfd",
                hit, true, width, height, session.FrameRate, PreviewPixelFormat.VfdPicture, 0, "vfd-native")
            : new RenderArtifact { SessionId = session.Id, ProjectRelativePath = relativePath, Width = width, Height = height,
                PixelFormat = PreviewPixelFormat.VfdPicture, CacheHit = hit, IsPreview = true };
    }
}
