using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Controls;
using projectFrameCut.Drawing.Base;
using projectFrameCut.LivePreview;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;
using System.Security.Cryptography;

namespace projectFrameCut.InteractableEditor
{
    public sealed partial class DynamicPreview
    {
        private static bool TryUpdatePreviewTreeInPlace(View existing, View incoming)
        {
            if (ReferenceEquals(existing, incoming))
            {
                return true;
            }

            if (existing.GetType() != incoming.GetType())
            {
                return false;
            }

            ApplySharedViewState(existing, incoming);

            switch (existing)
            {
                case HdrPreviewView existingHdr when incoming is HdrPreviewView incomingHdr:
                    existingHdr.Frame = incomingHdr.Frame;
                    return true;
                case Image existingImage when incoming is Image incomingImage:
                    existingImage.Aspect = incomingImage.Aspect;
                    existingImage.Source = incomingImage.Source;
                    return true;
                case ContentView existingContent when incoming is ContentView incomingContent:
                    if (incomingContent.Content is null)
                    {
                        existingContent.Content = null;
                        return true;
                    }

                    if (existingContent.Content is not View existingContentChild
                        || incomingContent.Content is not View incomingContentChild)
                    {
                        return false;
                    }

                    return TryUpdatePreviewTreeInPlace(existingContentChild, incomingContentChild);
                case Grid existingGrid when incoming is Grid incomingGrid:
                    if (existingGrid.Children.Count != incomingGrid.Children.Count)
                    {
                        return false;
                    }

                    for (var i = 0; i < existingGrid.Children.Count; i++)
                    {
                        if (existingGrid.Children[i] is not View existingGridChild
                            || incomingGrid.Children[i] is not View incomingGridChild
                            || !TryUpdatePreviewTreeInPlace(existingGridChild, incomingGridChild))
                        {
                            return false;
                        }
                    }

                    return true;
                default:
                    return false;
            }
        }

        private static void ApplySharedViewState(View target, View source)
        {
            target.WidthRequest = source.WidthRequest;
            target.HeightRequest = source.HeightRequest;
            target.MinimumWidthRequest = source.MinimumWidthRequest;
            target.MinimumHeightRequest = source.MinimumHeightRequest;
            target.MaximumWidthRequest = source.MaximumWidthRequest;
            target.MaximumHeightRequest = source.MaximumHeightRequest;
            target.HorizontalOptions = source.HorizontalOptions;
            target.VerticalOptions = source.VerticalOptions;
            target.Margin = source.Margin;
            target.InputTransparent = source.InputTransparent;
            target.AnchorX = source.AnchorX;
            target.AnchorY = source.AnchorY;
            target.Scale = source.Scale;
            target.ScaleX = source.ScaleX;
            target.ScaleY = source.ScaleY;
            target.TranslationX = source.TranslationX;
            target.TranslationY = source.TranslationY;
            target.Rotation = source.Rotation;
            target.RotationX = source.RotationX;
            target.RotationY = source.RotationY;
            target.Opacity = source.Opacity;
            target.IsVisible = source.IsVisible;
            target.ZIndex = source.ZIndex;
            target.AutomationId = source.AutomationId;
        }

        private static void SuppressCompositedPreviews(PreparedPreview[] previews)
        {
            IClip? top = null;
            foreach (var preview in previews)
            {
                if (preview.Source is { } clip && preview.ViewFactory is not null && preview.ErrorMessage is null
                    && PreviewRenderComposer.RequiresBackdrop(clip) && ClipArgumentBinding.ShouldRender(clip)
                    && (top is null || ClipLayerOrder.BottomToTop.Compare(clip, top) > 0))
                    top = clip;
            }
            if (top is null) return;
            for (var i = 0; i < previews.Length; i++)
            {
                var preview = previews[i];
                if (preview.Source is not { } clip || ClipLayerOrder.BottomToTop.Compare(clip, top) >= 0) continue;
                previews[i] = new PreparedPreview(preview.ClipId, () => new ContentView(), null, clip,
                    isTransparentAt: preview.IsTransparentAt, isPositionedClipPreview: preview.IsPositionedClipPreview);
            }
        }

        private static PreparedPreview CreatePreparedClipPreview(IClip clip, PreviewFrameSource frame, bool positioned)
        {
            frame = HdrPreviewView.PrepareFrame(frame);
#if WINDOWS
            if (frame.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb || frame.RequireSwapChain)
                return new PreparedPreview(clip.Id, () => new HdrPreviewView
                {
                    Frame = frame,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                    AutomationId = $"hdr-clip={clip.ClipType},id={clip.Id}",
                }, null, clip, isPositionedClipPreview: positioned, isTransparentAt: CreatePreviewTransparencyHitTest(frame));
#endif
            var bytes = frame.Png!;
            var source = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
            return new PreparedPreview(clip.Id, () => new PreviewImage
            {
                Source = source,
                Aspect = Aspect.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                AutomationId = $"clip={clip.ClipType},id={clip.Id}",
            }, null, clip, isPositionedClipPreview: positioned, isTransparentAt: CreatePreviewTransparencyHitTest(frame));
        }

        private static Func<double, double, bool>? CreatePreviewTransparencyHitTest(PreviewFrameSource frame)
        {
            if (frame.AlphaMask is { } mask)
                return (x, y) =>
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) return true;
                    int i = Math.Min(frame.Height - 1, (int)(y * frame.Height)) * frame.Width
                        + Math.Min(frame.Width - 1, (int)(x * frame.Width));
                    return (mask[i >> 3] & (1 << (i & 7))) != 0;
                };
            string path = frame.VfdPath;
            // Playback only needs the image; decode the alpha mask when it is first hit-tested.
            var hitTest = new Lazy<Func<double, double, bool>?>(() =>
            {
                try
                {
                    using var picture = PreviewFrameMaterializer.LoadVfd(path);
                    return CreateTransparencyHitTest(picture);
                }
                catch (Exception ex)
                {
                    Log(ex, $"Read preview transparency from {path}", typeof(DynamicPreview));
                    return null;
                }
            });
            return (x, y) => hitTest.Value?.Invoke(x, y) ?? false;
        }

        private static Func<double, double, bool>? CreateTransparencyHitTest(IPicture picture)
        {
            if (!picture.HasAlphaChannel
                || picture.GetSpecificChannel(IPicture.ChannelId.Alpha) is not Half[] alpha)
            {
                return null;
            }

            return CreateTransparencyHitTest(picture.Width, picture.Height, i => i < alpha.Length && Half.IsFinite(alpha[i]) && alpha[i] == Half.Zero);
        }

        private static Func<double, double, bool>? CreateTransparencyHitTest(int width, int height, Func<int, bool> isTransparent)
        {
            var pixelCount = checked(width * height);
            var mask = new byte[(pixelCount + 7) / 8];
            var hasTransparency = false;
            for (var i = 0; i < pixelCount; i++)
            {
                if (!isTransparent(i))
                {
                    continue;
                }

                mask[i >> 3] |= (byte)(1 << (i & 7));
                hasTransparency = true;
            }

            if (!hasTransparency)
            {
                return null;
            }

            return (x, y) =>
            {
                if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > 1 || y > 1)
                {
                    return true;
                }

                var px = Math.Min(width - 1, (int)(x * width));
                var py = Math.Min(height - 1, (int)(y * height));
                var i = py * width + px;
                return (mask[i >> 3] & (1 << (i & 7))) != 0;
            };
        }
    }
}

namespace projectFrameCut.LivePreview
{
    public enum NativePreviewOutputMode
    {
        Disabled,
        Automatic,
        Required,
    }

    public sealed record PreviewFrameSource(
        string VfdPath,
        int Width,
        int Height,
        PreviewPixelFormat TargetPixelFormat,
        bool RequireSwapChain)
    {
        internal ScRgbFrame? ScRgb { get; init; }
        internal byte[]? Png { get; init; }
        internal byte[]? AlphaMask { get; init; }
        public string PreviewContainer { get; init; } = string.Empty;
        public string PreviewEffectHash { get; init; } = string.Empty;
    }

    internal static class PreviewFrameMaterializer
    {
        public static IPicture LoadVfd(string path)
        {
            if (PreviewFrameContainer.IsPreviewContainer(path))
                throw new InvalidDataException("A preview container's source picture is not its display result.");
            using var stream = PreviewFrameContainer.Open(path);
            if (!PictureExtensions.SharedVfdPictureDecoder.TryLoadFP16(stream, out var picture) || picture is null)
                throw new InvalidDataException($"Preview source '{path}' is not a valid VFD picture.");
            return picture;
        }

        public static PreviewFrameSource Prepare(PreviewFrameSource frame, PreviewFrameBuffer? buffer = null)
        {
            if (frame.ScRgb is { IsCurrent: true } || frame.Png is not null) return frame;
            Func<int, byte[]>? allocatePixels = buffer is null ? null : buffer.GetBytes;
            if (frame.PreviewContainer is "vfd2m" or PreviewFrameContainer.ViewContainer || PreviewFrameContainer.IsPreviewContainer(frame.VfdPath))
            {
                var data = PreviewFrameContainer.Read(frame.VfdPath, frame.PreviewEffectHash,
                    frame.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb, allocatePixels);
                return FromData(frame, data, buffer);
            }
            using var picture = LoadVfd(frame.VfdPath);
            return FromData(frame, PreviewFrameData.Create(picture, "", "",
                frame.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb, allocatePixels), buffer);
        }

        public static PreviewFrameSource FromData(PreviewFrameSource frame, PreviewFrameData data, PreviewFrameBuffer? buffer = null)
            => frame with
            {
                Width = data.Width, Height = data.Height, AlphaMask = data.AlphaMask,
                PreviewEffectHash = data.EffectHash,
                ScRgb = data.ScRgb ? new(data.Pixels, data.Width, data.Height, data.Stride, data.HasTransparency, data.ContentHash)
                    { Buffer = buffer, BufferVersion = buffer?.Version ?? 0 } : null,
                Png = data.ScRgb ? null : data.Pixels,
            };

        public static byte[] ToPngBytes(string path)
        {
            if (PreviewFrameContainer.IsPreviewContainer(path))
                return PreviewFrameContainer.Read(path).ToPng();
            using var picture = LoadVfd(path);
            return ToPngBytes(picture);
        }

        public static byte[] ToPngBytes(IPicture picture) => PreviewFrameData.ToPng(picture);

        public static byte[] ToPngBytes(PreviewFrameSource frame)
        {
            if (frame.Png is not null) return frame.Png;
            if (frame.ScRgb is { } pixels)
            {
                lock (pixels.PixelsGate)
                {
                    if (pixels.IsCurrent)
                        return new PreviewFrameData("", frame.PreviewEffectHash, true, pixels.Width, pixels.Height, pixels.Stride,
                            pixels.HasTransparency, pixels.ContentHash, pixels.Bytes, frame.AlphaMask ?? []).ToPng();
                }
            }
            return ToPngBytes(frame.VfdPath);
        }

        public static ScRgbFrame ToScRgb(string path)
        {
            if (PreviewFrameContainer.IsPreviewContainer(path))
            {
                var data = PreviewFrameContainer.Read(path, scRgb: true);
                return new(data.Pixels, data.Width, data.Height, data.Stride, data.HasTransparency, data.ContentHash);
            }
            using var picture = LoadVfd(path);
            return ToScRgb(picture);
        }

        public static ScRgbFrame ToScRgb(IPicture picture)
        {
            var data = PreviewFrameData.Create(picture, "", "", true);
            return new(data.Pixels, data.Width, data.Height, data.Stride, data.HasTransparency, data.ContentHash);
        }

        public static ImageSource CreateImageSource(string path)
        {
            var bytes = ToPngBytes(path);
            return ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
        }
    }

    internal sealed record ScRgbFrame(byte[] Bytes, int Width, int Height, int Stride, bool HasTransparency, string? CachedContentHash = null)
    {
        public string ContentHash { get; } = CachedContentHash ?? Convert.ToHexString(SHA256.HashData(Bytes));
        internal PreviewFrameBuffer? Buffer { get; init; }
        internal long BufferVersion { get; init; }
        internal object PixelsGate => Buffer?.Gate ?? this;
        internal bool IsCurrent => Buffer is null || Volatile.Read(ref Buffer.Version) == BufferVersion;
    }

    internal sealed class PreviewFrameBuffer
    {
        public object Gate { get; } = new();
        public long Version;
        private byte[] bytes = [];

        public byte[] GetBytes(int length)
        {
            if (bytes.Length != length) bytes = GC.AllocateUninitializedArray<byte>(length);
            return bytes;
        }
    }
}
