using System.Diagnostics;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;

namespace projectFrameCut.Render.Rendering;

/// <summary>Delivers clip-local pictures borrowed for the duration of each callback.</summary>
public sealed class PreviewRenderComposer(
    Action<uint, RenderClipContent, CancellationToken> receiveClip,
    Action<uint, CancellationToken>? completeFrame = null) : RenderComposerBase
{
    protected override bool SeparateOutput => true;

    protected override IPicture? ComposeFrame(RenderCompositionFrame frame, IReadOnlyDictionary<Guid, RenderClipContent?> clips,
        IPicture blank, CancellationToken token)
    {
        var prepared = new List<RenderClipContent>();
        try
        {
            foreach (var id in frame.Clips)
            {
                token.ThrowIfCancellationRequested();
                var clip = clips[id];
                if (clip is null)
                {
                    var source = ClipArgumentBinding.InitializeFrame(Options.Clips.First(c => c.Id == id), frame.FrameIndex);
                    var transparent = Timeline.CreateTransparentFrame(
                        source.TargetWidth > 0 ? Math.Max(1, (int)Math.Round((double)source.TargetWidth * Options.TargetWidth / Math.Max(1, Options.ProjectRelativeWidth), MidpointRounding.AwayFromZero)) : Options.TargetWidth,
                        source.TargetHeight > 0 ? Math.Max(1, (int)Math.Round((double)source.TargetHeight * Options.TargetHeight / Math.Max(1, Options.ProjectRelativeHeight), MidpointRounding.AwayFromZero)) : Options.TargetHeight,
                        IPicture.PicturePixelMode.Float16Picture);
                    prepared.Add(new(source, transparent, default, false));
                    continue;
                }
                if (ProjectReferenceRuntime.IsCanvasClip(clip.Clip)) clip = clip with { IsCanvas = true };
                var picture = FrameComposition.Compose([clip], frame.FrameIndex, Options, blank, token,
                    clipLocalOutput: true, keepHwAccelPicture: true);
                prepared.Add(clip with { Picture = picture });
            }
            frame.PreviewClips = prepared.ToArray();
            return null;
        }
        catch
        {
            foreach (var clip in prepared) clip.Picture.Dispose();
            throw;
        }
    }

    protected override void StoreFrame(RenderCompositionFrame frame, IPicture? picture, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            foreach (var clip in frame.PreviewClips ?? [])
            {
                token.ThrowIfCancellationRequested();
                receiveClip(frame.FrameIndex, clip, token);
            }
            completeFrame?.Invoke(frame.FrameIndex, token);
            NotifyWritten(frame, Stopwatch.GetElapsedTime(started));
            if (frame.Diagnostics is not null) frame.Diagnostics.Succeeded = true;
        }
        finally
        {
            picture?.Dispose();
            frame.Dispose();
        }
    }
}
