using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
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

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
        };

        public string? NeedComputer => "PlaceComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

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
        public string? BindedEffectGroupID { get; set; }

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

            return new PlaceEffect_HwAccel
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            }

            int startX = StartX;
            int startY = StartY;
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
                return EffectHelper.PlacePicture(source, startX, startY, targetWidth, targetHeight, "Place", typeof(PlaceEffect_HwAccel));
            }

            var sw = Stopwatch.StartNew();
            var (r, g, b, a) = ExtractFloatChannels(source);
            var resultArr = computer.Compute([
                r,
                g,
                b,
                a,
                source.Width,
                source.Height,
                startX,
                startY,
                targetWidth,
                targetHeight
            ]);

            if (resultArr.Length != 4 ||
                resultArr[0] is not float[] rOut ||
                resultArr[1] is not float[] gOut ||
                resultArr[2] is not float[] bOut ||
                resultArr[3] is not float[] aOut)
            {
                throw new InvalidOperationException("PlaceComputer did not return expected channel buffers.");
            }

            var result = BuildPicture(source, targetWidth, targetHeight, rOut, gOut, bOut, aOut);
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

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
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

    public class PlaceEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Place";

        public EffectTarget Target => EffectTarget.Video;

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int"},
            {"StartY", "int"},
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.HwAcceleration };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.HwAcceleration => PlaceEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters)
        {
            Log("Place and Resize effects are deprecated. Consider migrate to IClipPositionProvider.", "warn");
            return PlaceEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }
    }
}
