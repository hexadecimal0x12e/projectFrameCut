using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private async ValueTask<TimelineThumbnailsResponse> GenerateTimelineThumbnailsAsync(TimelineThumbnailsRequest request, CancellationToken cancellationToken)
    {
        if (request.ClipId == Guid.Empty || request.TotalFrames <= 0 || request.IntervalFrames <= 0
            || request.MaxCount is <= 0 or > 60 || request.Width is <= 0 or > 4096)
            throw new ArgumentException("Invalid timeline thumbnail parameters.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        return await Task.Run(() =>
        {
            var result = new TimelineThumbnailsResponse();
            using var effects = new EffectRuntimeContext();
            using var effectScope = effects.Enter();
            using var source = PluginManager.CreateVideoSource(ResolveProjectSourcePath(session.ProjectRoot, request.SourcePath)!);
            cancellationToken.ThrowIfCancellationRequested();
            source.Initialize();
            try
            {
                for (int i = 0; i < request.MaxCount; i++)
                {
                    long frameIndex = (long)i * request.IntervalFrames;
                    if (frameIndex >= request.TotalFrames) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = $"thumbs/perClip/{request.ClipId}/timeline/{frameIndex}.png";
                    var path = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
                    string? temporaryPath = null;
                    try
                    {
                        var cacheHit = File.Exists(path);
                        if (!cacheHit)
                        {
                            using var frame = source.GetFrame((uint)frameIndex);
                            using var resized = frame.Resize(request.Width, Math.Max(1, (int)((double)frame.Height * request.Width / frame.Width)), false);
                            temporaryPath = _artifacts.CreateTemporaryPath(path);
                            if (resized is IHDRPicture<Half> hdr)
                            {
                                using var sdr = hdr.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB);
                                sdr.SaveToPng(temporaryPath);
                            }
                            else resized.SaveToPng(temporaryPath);
                            cancellationToken.ThrowIfCancellationRequested();
                            _artifacts.CommitTemporaryFile(temporaryPath, path);
                        }
                        result.Artifacts.Add(_artifacts.Register(session.Id, session.ProjectRoot, relativePath, "image/png", cacheHit, true,
                            pixelFormat: PreviewPixelFormat.PngImage));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log(ex, $"Generate timeline thumbnail clip={request.ClipId} frame={frameIndex}", this);
                        result.Errors.Add(new RemoteError(ex));
                    }
                    finally
                    {
                        try { if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                        catch (Exception ex) { Log(ex, "Delete temporary timeline thumbnail", this); }
                    }
                }
                return result;
            }
            catch
            {
                foreach (var artifact in result.Artifacts) _artifacts.Release(session.Id, artifact.ArtifactId);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
