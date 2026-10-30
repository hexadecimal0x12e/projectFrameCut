using projectFrameCut.Render.Contracts;
using projectFrameCut.Services;

namespace projectFrameCut.LivePreview;

public partial class LivePreviewer
{
    public async Task<string> ExportAudioSamplesAsync(uint start, uint count, int sampleRate, int channels, CancellationToken token)
    {
        await _updateDraftGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var client = RpcClient ?? RenderRpcBootstrap.Client;
            var artifact = await client.RenderAudioSegmentAsync(new()
            {
                SessionId = RenderSessionId, StartFrame = start, Length = count,
                FrameRate = targetFrameRate, SampleRate = sampleRate, Channels = channels,
            }, token).ConfigureAwait(false);
            try { return await ResolveArtifactPathAsync(artifact, token).ConfigureAwait(false); }
            finally
            {
                try
                {
                    using var release = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await client.ReleaseArtifactAsync(new() { SessionId = artifact.SessionId, ArtifactId = artifact.ArtifactId }, release.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Log(ex, "Release exported audio artifact", this); }
            }
        }
        finally { _updateDraftGate.Release(); }
    }
}
