using projectFrameCut.Render.Contracts;
using projectFrameCut.Services;

namespace projectFrameCut.LivePreview;

public partial class LivePreviewer
{
    public async Task<string[]> ExportFrameSequenceAsync(uint start, uint count, int width, int height,
        FrameSequenceRenderOptions options, Func<RenderJob, Task> progress, CancellationToken token)
    {
        await _updateDraftGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var client = RpcClient ?? RenderRpcBootstrap.Client;
            var job = await client.RenderProjectAsync(new()
            {
                SessionId = RenderSessionId, ExportFrameSequence = true,
                StartFrame = start, FrameCount = count, Width = width, Height = height,
                FrameRate = targetFrameRate, IncludeAudio = false, FrameSequenceOptions = options,
            }, token).ConfigureAwait(false);
            try
            {
                while (job.State is RenderJobState.Queued or RenderJobState.Running)
                {
                    await progress(job).ConfigureAwait(false);
                    await Task.Delay(500, token).ConfigureAwait(false);
                    job = await client.GetJobStatusAsync(job.JobId, token).ConfigureAwait(false);
                }
                if (job.Error is not null) throw new RenderRpcException(job.Error);
                if (job.State != RenderJobState.Completed) throw new OperationCanceledException("Preview frame rendering was canceled.");
                var paths = new List<string>();
                foreach (var artifact in job.FrameArtifacts)
                {
                    try { paths.Add(await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false)); }
                    finally
                    {
                        using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await client.ReleaseArtifactAsync(new() { SessionId = artifact.SessionId, ArtifactId = artifact.ArtifactId }, release.Token).ConfigureAwait(false);
                    }
                }
                return paths.ToArray();
            }
            finally
            {
                if (job.State is RenderJobState.Queued or RenderJobState.Running)
                {
                    try
                    {
                        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await client.CancelJobAsync(job.JobId, cancel.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Log(ex, "Cancel preview frame rendering", this); }
                }
            }
        }
        finally { _updateDraftGate.Release(); }
    }
}
