using System;
using System.Collections.Generic;
using System.Text;

using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider
{
    internal abstract class InternalEffectDynamicPreviewProviderBase : IEffectDynamicPreviewProvider
    {
        public abstract string TypeName { get; }

        public virtual bool IsAvailable(IEffect target, Type typeOfInput)
        {
            return target.FromPlugin == InternalPluginBase.InternalPluginBaseID
                && typeof(View).IsAssignableFrom(typeOfInput);
        }

        public abstract View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress);

        protected static bool TryGetParameter<T>(IEffect target, string key, out T value)
        {
            value = default!;
            if (!target.Parameters.TryGetValue(key, out var raw) || raw is null)
            {
                return false;
            }

            raw = NormalizeJsonElement(raw);

            if (raw is T typed)
            {
                value = typed;
                return true;
            }

            try
            {
                var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                var converted = Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
                if (converted is T convertedTyped)
                {
                    value = convertedTyped;
                    return true;
                }

                value = (T)converted!;
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected static object NormalizeJsonElement(object raw)
        {
            if (raw is not JsonElement element)
            {
                return raw;
            }

            return element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out var i64)
                    ? i64
                    : element.TryGetDouble(out var d)
                        ? d
                        : element.ToString(),
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => element.ToString(),
            };
        }

        protected static int ScaleByCanvas(int value, IEffect target, int canvasSize, bool isWidth)
        {
            var relative = isWidth ? target.RelativeWidth : target.RelativeHeight;
            if (relative > 0 && relative != canvasSize)
            {
                return (int)Math.Round((double)value * canvasSize / relative);
            }
            return value;
        }

        protected static View WrapForCrop(View input, double width, double height, double shiftX, double shiftY)
        {
            var host = new Grid
            {
                WidthRequest = Math.Max(1, width),
                HeightRequest = Math.Max(1, height),
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                IsClippedToBounds = true
            };

            host.Add(input);
            input.TranslationX = shiftX;
            input.TranslationY = shiftY;
            return host;
        }
    }

    internal sealed class BlurEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Blur";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var sigma = 0f;
            TryGetParameter<float>(target, "Sigma", out sigma);
            visual.Shadow = new Shadow
            {
                Radius = Math.Clamp(sigma * 2f, 0f, 40f),
                Offset = new Point(0, 0),
                Brush = Brush.Black,
                Opacity = sigma > 0f ? 0.6f : 0f,
            };
            return input;
        }
    }

    internal sealed class RemoveColorEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "RemoveColor";

        public override bool IsAvailable(IEffect target, Type typeOfInput)
        {
            return base.IsAvailable(target, typeOfInput) && typeof(Image).IsAssignableFrom(typeOfInput);
        }

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is Image image)
            {
                // Keep clip preview fully opaque; fake global opacity makes VideoClip appear unintentionally translucent.
                image.Opacity = 1d;
            }
            return input;
        }
    }

    internal sealed class JitterEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Jitter";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var maxX = 0;
            var maxY = 0;
            var seed = 0;
            var direction = "Both";
            TryGetParameter<int>(target, "MaxOffsetX", out maxX);
            TryGetParameter<int>(target, "MaxOffsetY", out maxY);
            TryGetParameter<int>(target, "Seed", out seed);
            TryGetParameter<string>(target, "Direction", out direction);

            maxX = ScaleByCanvas(maxX, target, canvasWidth, isWidth: true);
            maxY = ScaleByCanvas(maxY, target, canvasHeight, isWidth: false);

            var rnd = new Random(unchecked(seed + (int)targetFrame * 397));
            var allowX = direction == "Both" || direction == "XOnly";
            var allowY = direction == "Both" || direction == "YOnly";
            var x = allowX && maxX > 0 ? rnd.Next(-maxX, maxX + 1) : 0;
            var y = allowY && maxY > 0 ? rnd.Next(-maxY, maxY + 1) : 0;

            visual.TranslationX += x;
            visual.TranslationY += y;
            return input;
        }
    }

    internal sealed class ZoomInEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "ZoomIn";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var targetX = canvasWidth;
            var targetY = canvasHeight;
            TryGetParameter<int>(target, "TargetX", out targetX);
            TryGetParameter<int>(target, "TargetY", out targetY);

            targetX = Math.Max(1, ScaleByCanvas(targetX, target, canvasWidth, isWidth: true));
            targetY = Math.Max(1, ScaleByCanvas(targetY, target, canvasHeight, isWidth: false));

            var goalScaleX = Math.Max(1d, (double)canvasWidth / targetX);
            var goalScaleY = Math.Max(1d, (double)canvasHeight / targetY);
            var goalScale = Math.Min(8d, Math.Max(goalScaleX, goalScaleY));

            var scale = 1d + (goalScale - 1d) * progress;

            visual.AnchorX = 0.5;
            visual.AnchorY = 0.5;
            visual.Scale = scale;
            return input;
        }
    }

    internal sealed class PlaceEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Place";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var startX = 0;
            var startY = 0;
            TryGetParameter<int>(target, "StartX", out startX);
            TryGetParameter<int>(target, "StartY", out startY);

            startX = ScaleByCanvas(startX, target, canvasWidth, isWidth: true);
            startY = ScaleByCanvas(startY, target, canvasHeight, isWidth: false);

            visual.TranslationX += startX;
            visual.TranslationY += startY;
            return input;
        }
    }

    internal sealed class RotationEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Rotation";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var angle = 0f;
            TryGetParameter<float>(target, "Angle", out angle);
            visual.Rotation += angle;
            return input;
        }
    }

    internal sealed class ResizeEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Resize";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var width = canvasWidth;
            var height = canvasHeight;
            var preserveAspect = true;
            TryGetParameter<int>(target, "Width", out width);
            TryGetParameter<int>(target, "Height", out height);
            TryGetParameter<bool>(target, "PreserveAspectRatio", out preserveAspect);

            width = Math.Max(1, ScaleByCanvas(width, target, canvasWidth, isWidth: true));
            height = Math.Max(1, ScaleByCanvas(height, target, canvasHeight, isWidth: false));

            if (preserveAspect)
            {
                var srcRatio = (double)canvasWidth / Math.Max(1, canvasHeight);
                var requestedRatio = (double)width / height;
                if (requestedRatio > srcRatio)
                {
                    width = (int)Math.Round(height * srcRatio);
                }
                else
                {
                    height = (int)Math.Round(width / srcRatio);
                }
            }

            visual.WidthRequest = width;
            visual.HeightRequest = height;
            return input;
        }
    }

    internal sealed class CropEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Crop";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            var startX = 0;
            var startY = 0;
            var width = canvasWidth;
            var height = canvasHeight;
            List<CropData>? cropList = null;
            TryGetParameter<int>(target, "StartX", out startX);
            TryGetParameter<int>(target, "StartY", out startY);
            TryGetParameter<int>(target, "Width", out width);
            TryGetParameter<int>(target, "Height", out height);
            if (TryGetParameter<string>(target, "CropList", out var cropListJson) && !string.IsNullOrWhiteSpace(cropListJson))
            {
                cropList = JsonSerializer.Deserialize<List<CropData>>(cropListJson);
                if (cropList is not null && cropList.Count > 1)
                {
                    cropList.Sort((a, b) => a.Index.CompareTo(b.Index));
                }
            }

            if (cropList is not null && cropList.Count > 0)
            {
                var crop = ResolveCrop(cropList, (double)progress, startX, startY, width, height);
                startX = crop.StartX;
                startY = crop.StartY;
                width = crop.Width;
                height = crop.Height;
            }

            startX = ScaleByCanvas(startX, target, canvasWidth, isWidth: true);
            startY = ScaleByCanvas(startY, target, canvasHeight, isWidth: false);
            width = Math.Max(1, ScaleByCanvas(width, target, canvasWidth, isWidth: true));
            height = Math.Max(1, ScaleByCanvas(height, target, canvasHeight, isWidth: false));

            return WrapForCrop(input, width, height, -startX, -startY);
        }

        private static CropData ResolveCrop(List<CropData> cropList, double progress, int fallbackStartX, int fallbackStartY, int fallbackWidth, int fallbackHeight)
        {
            if (cropList.Count == 0)
            {
                return new CropData(progress, fallbackStartX, fallbackStartY, fallbackWidth, fallbackHeight);
            }

            if (cropList.Count == 1)
            {
                return cropList[0];
            }

            if (progress <= cropList[0].Index)
            {
                return cropList[0];
            }

            int lastIndex = cropList.Count - 1;
            if (progress >= cropList[lastIndex].Index)
            {
                return cropList[lastIndex];
            }

            for (int i = 1; i < cropList.Count; i++)
            {
                var current = cropList[i];
                if (progress <= current.Index)
                {
                    var previous = cropList[i - 1];
                    double span = current.Index - previous.Index;
                    if (span <= 0)
                    {
                        return current;
                    }

                    double t = (progress - previous.Index) / span;
                    int x = (int)Math.Round(previous.StartX + (current.StartX - previous.StartX) * t);
                    int y = (int)Math.Round(previous.StartY + (current.StartY - previous.StartY) * t);
                    int w = (int)Math.Round(previous.Width + (current.Width - previous.Width) * t);
                    int h = (int)Math.Round(previous.Height + (current.Height - previous.Height) * t);
                    float angle = (float)(previous.Angle + (current.Angle - previous.Angle) * t);
                    return new CropData(progress, x, y, w, h, angle);
                }
            }

            return cropList[lastIndex];
        }
    }

    internal sealed class FlipEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Flip";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var horizontal = false;
            var vertical = false;
            TryGetParameter<bool>(target, "Horizontal", out horizontal);
            TryGetParameter<bool>(target, "Vertical", out vertical);

            visual.ScaleX *= horizontal ? -1 : 1;
            visual.ScaleY *= vertical ? -1 : 1;
            return input;
        }
    }

    internal sealed class SharpenEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Sharpen";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            return input;
        }
    }

    internal sealed class VignetteEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "Vignette";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            return input;
        }
    }

    internal sealed class FadeOpacityEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "FadeOpacity";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            if (input is not VisualElement visual)
            {
                return input;
            }

            var opacity = 1f;
            TryGetParameter<float>(target, "Opacity", out opacity);
            visual.Opacity = Math.Clamp(visual.Opacity * opacity, 0d, 1d);
            return input;
        }
    }

    internal sealed class PointPlacerEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "PointPlacer";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            return input;
        }
    }

    internal sealed class StraightLineMovementValueProducerEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "StraightLineMovementValueProducer";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            return input;
        }
    }

    internal sealed class SubjectMattingMaskGeneratorEffectDynamicPreviewProvider : InternalEffectDynamicPreviewProviderBase
    {
        public override string TypeName => "SubjectMattingMaskGenerator";

        public override View Generate(IEffect target, View input, Type typeOfInput, int canvasWidth, int canvasHeight, uint targetFrame, float progress)
        {
            return input;
        }
    }
}
