using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class PlaceEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Place";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; } = string.Empty;

        public int StartX { get; set; }
        public int StartY { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string? NeedComputer => "PlaceComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY"
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
        };

        public string TypeName => "Place";
        public string? BindedEffectProvidingSystemID { get; set; }

        void IEffect.Initialize()
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
        }

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            if (parameters.Count != ParametersNeeded.Count)
            {
                throw new ArgumentException("Too many parameters provided.");
            }

            var effect = new PlaceEffect_HwAccel
            {
                StartX = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartX")),
                StartY = DynamicParam.ToInt32(parameters.GetValueOrDefault("StartY")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            }

            int placeX = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartX"), StartX);
            int placeY = DynamicParam.Resolve(Parameters.GetValueOrDefault("StartY"), StartY);
            int startX = placeX;
            int startY = placeY;
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)startX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)startY * targetHeight / RelativeHeight);
            }

            return RenderWithOffset(source, computer, startX, startY, targetWidth, targetHeight);
        }

        internal static IPicture RenderWithOffset(IPicture source, IComputer? computer, int startX, int startY, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            }

            if (computer is null)
            {
                return PlaceEffect.Process(source, startX, startY, targetWidth, targetHeight);
            }

            var sw = Stopwatch.StartNew();
            var (r, g, b, a) = ExtractFloatChannels(source);

            FourChannelResult placeResult;
            if (computer is IPlaceComputer pc)
            {
                placeResult = pc.ComputePlace(r, g, b, a, source.Width, source.Height,
                    startX, startY, targetWidth, targetHeight);
            }
            else
            {
                var resultArr = computer.Compute([
                    r, g, b, a,
                    source.Width, source.Height,
                    startX, startY, targetWidth, targetHeight
                ]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("PlaceComputer did not return expected channel buffers.");
                }

                placeResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = BuildPicture(source, targetWidth, targetHeight,
                placeResult.R, placeResult.G, placeResult.B, placeResult.A);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Place (GPU)",
                Operator = typeof(PlaceEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "StartX", startX },
                    { "StartY", startY },
                    { "TargetWidth", targetWidth },
                    { "TargetHeight", targetHeight }
                }
            }).ToList();

            return result;
        }


        private static (float[] r, float[] g, float[] b, float[] a) ExtractFloatChannels(IPicture source)
        {
            if (source is IPicture<ushort> p16)
            {
                return (
                    p16.r.Select(Convert.ToSingle).ToArray(),
                    p16.g.Select(Convert.ToSingle).ToArray(),
                    p16.b.Select(Convert.ToSingle).ToArray(),
                    p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray()
                );
            }

            if (source is IPicture<byte> p8)
            {
                return (
                    p8.r.Select(Convert.ToSingle).ToArray(),
                    p8.g.Select(Convert.ToSingle).ToArray(),
                    p8.b.Select(Convert.ToSingle).ToArray(),
                    p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray()
                );
            }

            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
        }

        private static IPicture BuildPicture(IPicture source, int width, int height, float[] r, float[] g, float[] b, float[] a)
        {
            if (source.BitPerPixel == 16)
            {
                var picture = new Picture16bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = true,
                };
                picture.r = r.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.g = g.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.b = b.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.a = a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray();
                return picture;
            }

            if (source.BitPerPixel == 8)
            {
                var picture = new Picture8bpp(width, height)
                {
                    Tag = source.Tag,
                    HasAlphaChannel = true,
                };
                picture.r = r.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.g = g.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.b = b.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.a = a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray();
                return picture;
            }

            throw new NotSupportedException($"Specific pixel-mode is not supported.");
        }

    }

    /// <summary>
    /// The Render-side provider of the Place effect.
    /// </summary>
    public class PlaceEffectProvider : EffectProviderBase
    {
        public PlaceEffectProvider()
        {
            Name = "Place";
            SetField("StartX", 0);
            SetField("StartY", 0);
        }

        public override string TypeName => "Place";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("StartX", EffectArgumentFieldType.Integer, "0"),
                Field("StartY", EffectArgumentFieldType.Integer, "0")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
            if (implementType == EffectImplementType.NotSpecified)
            {
                return [PlaceEffect_HwAccel.FromParametersDictionary(parameters)];
            }
            return implementType switch
            {
                EffectImplementType.HwAcceleration => [PlaceEffect_HwAccel.FromParametersDictionary(parameters)],
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }
    }
}
