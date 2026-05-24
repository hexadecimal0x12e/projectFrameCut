using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace projectFrameCut.Render.Effect
{
    public class CropEffect_ImageSharp : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"StartX", StartX },
            {"StartY", StartY },
            {"Height", Height },
            {"Width", Width },
            {"Angle", Angle },
        };

        public string? NeedComputer => null;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
            "Angle"
        };

        public static List<string> OptionalParameters { get; } = new List<string>
        {
            "Angle",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"Height", "int" },
            {"Width", "int" },
            {"Angle", "float" },
        };

        public string TypeName => "Crop";

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.Except(OptionalParameters).All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var unsupportedParameters = parameters.Keys.Except(ParametersNeeded).Except(OptionalParameters).ToList();
            if (unsupportedParameters.Count > 0)
            {
                throw new ArgumentException($"Unsupported parameters: {string.Join(", ", unsupportedParameters)}");
            }

            float angle = parameters.TryGetValue("Angle", out var angleVal) ? Convert.ToSingle(angleVal) : 0f;

            return new CropEffect_ImageSharp
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = angle,
                ImplementType = implementType,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
            => Crop(source, StartX, StartY, Width, Height, targetWidth, targetHeight);

        public IPicture Crop(IPicture source, int startX, int startY, int width, int height, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return imgCtx =>
            {
                var safeRect = BuildSafeCropRect(StartX, StartY, Width, Height, imgCtx.GetCurrentSize());
                if (Math.Abs(Angle) <= float.Epsilon)
                {
                    return imgCtx.Crop(safeRect);
                }

                float centerX = safeRect.X + safeRect.Width / 2f;
                float centerY = safeRect.Y + safeRect.Height / 2f;
                var transformBuilder = new AffineTransformBuilder()
                    .AppendTranslation(new Vector2(-centerX, -centerY))
                    .AppendRotationDegrees(-Angle)
                    .AppendTranslation(new Vector2(centerX, centerY));

                return imgCtx.Transform(transformBuilder).Crop(safeRect);
            };
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            int startX = StartX, startY = StartY, width = Width, height = Height;
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)StartX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)StartY * targetHeight / RelativeHeight);
                width = (int)Math.Round((double)Width * targetWidth / RelativeWidth);
                height = (int)Math.Round((double)Height * targetHeight / RelativeHeight);
            }


            return new CropProcessStep(startX, startY, width, height, Angle);
        }

        private static Rectangle BuildSafeCropRect(int startX, int startY, int width, int height, Size currentSize)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }

            var requestedRect = new Rectangle(startX, startY, width, height);
            var sourceRect = new Rectangle(0, 0, currentSize.Width, currentSize.Height);
            var safeRect = Rectangle.Intersect(requestedRect, sourceRect);
            if (safeRect.Width <= 0 || safeRect.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startX), "Crop rectangle must overlap source bounds.");
            }

            return safeRect;
        }

        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; }
    }

    public class CropProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Crop";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public float Angle { get; init; }

        public CropProcessStep(int startX, int startY, int width, int height, float angle = 0f)
        {
            StartX = startX;
            StartY = startY;
            Width = width;
            Height = height;
            Angle = angle;
            Properties = new Dictionary<string, object?>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(Width), Width },
                { nameof(Height), Height },
                { nameof(Angle), Angle }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            IPicture result;
            if (Math.Abs(Angle) <= float.Epsilon)
            {
                result = EffectHelper.CropPicture(source, StartX, StartY, Width, Height, "Crop", typeof(CropProcessStep));
            }
            else
            {
                var img = source.SaveToSixLaborsImage();
                var safeRect = BuildSafeCropRect(StartX, StartY, Width, Height, img.Size);
                float centerX = safeRect.X + safeRect.Width / 2f;
                float centerY = safeRect.Y + safeRect.Height / 2f;
                var transformBuilder = new AffineTransformBuilder()
                    .AppendTranslation(new Vector2(-centerX, -centerY))
                    .AppendRotationDegrees(-Angle)
                    .AppendTranslation(new Vector2(centerX, centerY));

                var transformed = img.Clone(ctx => ctx.Transform(transformBuilder).Crop(safeRect));
                result = (int)source.bitPerPixel switch
                {
                    8 => new Picture8bpp(transformed),
                    16 => new Picture16bpp(transformed),
                    _ => throw new NotSupportedException("Specific pixel-mode is not supported.")
                };
            }

            sw.Stop();
            _elapsed = sw.Elapsed;
            result.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return imgCtx =>
            {
                var safeRect = BuildSafeCropRect(StartX, StartY, Width, Height, imgCtx.GetCurrentSize());
                if (Math.Abs(Angle) <= float.Epsilon)
                {
                    return imgCtx.Crop(safeRect);
                }

                float centerX = safeRect.X + safeRect.Width / 2f;
                float centerY = safeRect.Y + safeRect.Height / 2f;
                var transformBuilder = new AffineTransformBuilder()
                    .AppendTranslation(new Vector2(-centerX, -centerY))
                    .AppendRotationDegrees(-Angle)
                    .AppendTranslation(new Vector2(centerX, centerY));

                return imgCtx.Transform(transformBuilder).Crop(safeRect);
            };
        }

        private static Rectangle BuildSafeCropRect(int startX, int startY, int width, int height, Size currentSize)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }

            var requestedRect = new Rectangle(startX, startY, width, height);
            var sourceRect = new Rectangle(0, 0, currentSize.Width, currentSize.Height);
            var safeRect = Rectangle.Intersect(requestedRect, sourceRect);
            if (safeRect.Width <= 0 || safeRect.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startX), "Crop rectangle must overlap source bounds.");
            }

            return safeRect;
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Crop",
            Operator = typeof(CropProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(StartX), StartX },
                { nameof(StartY), StartY },
                { nameof(Width), Width },
                { nameof(Height), Height },
                { nameof(Angle), Angle }
            }
        };
    }

    public class CropEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Crop";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public int StartX { get; init; }
        public int StartY { get; init; }
        public int Height { get; init; }
        public int Width { get; init; }
        public float Angle { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
            { "Height", Height },
            { "Width", Width },
            { "Angle", Angle },
        };

        public string? NeedComputer => "CropComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
            "Angle"
        };

        public static List<string> OptionalParameters { get; } = new List<string>
        {
            "Angle",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "StartX", "int" },
            { "StartY", "int" },
            { "Height", "int" },
            { "Width", "int" },
            { "Angle", "float" },
        };

        public string TypeName => "Crop";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.Except(OptionalParameters).All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var unsupportedParameters = parameters.Keys.Except(ParametersNeeded).Except(OptionalParameters).ToList();
            if (unsupportedParameters.Count > 0)
            {
                throw new ArgumentException($"Unsupported parameters: {string.Join(", ", unsupportedParameters)}");
            }

            float angle = parameters.TryGetValue("Angle", out var angleVal) ? Convert.ToSingle(angleVal) : 0f;

            return new CropEffect_HwAccel
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                Angle = angle,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            int startX = StartX;
            int startY = StartY;
            int width = Width;
            int height = Height;

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }

            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)StartX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)StartY * targetHeight / RelativeHeight);
                width = (int)Math.Round((double)Width * targetWidth / RelativeWidth);
                height = (int)Math.Round((double)Height * targetHeight / RelativeHeight);
            }

            if (Math.Abs(Angle) > float.Epsilon)
            {
                return new CropProcessStep(startX, startY, width, height, Angle).Process(source);
            }

            var safeRect = BuildSafeCropRect(startX, startY, width, height, source.Width, source.Height);
            if (computer is null)
            {
                return EffectHelper.CropPicture(source, safeRect.X, safeRect.Y, safeRect.Width, safeRect.Height, "Crop", typeof(CropEffect_HwAccel));
            }

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = ExtractFloatChannels(source);
            var resultArr = computer.Compute([
                r,
                g,
                b,
                a,
                source.Width,
                source.Height,
                safeRect.X,
                safeRect.Y,
                safeRect.Width,
                safeRect.Height
            ]);

            if (resultArr.Length != 4 ||
                resultArr[0] is not float[] rOut ||
                resultArr[1] is not float[] gOut ||
                resultArr[2] is not float[] bOut ||
                resultArr[3] is not float[] aOut)
            {
                throw new InvalidOperationException("CropComputer did not return expected channel buffers.");
            }

            var result = BuildPicture(source, safeRect.Width, safeRect.Height, rOut, gOut, bOut, aOut, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Crop (GPU)",
                Operator = typeof(CropEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "StartX", safeRect.X },
                    { "StartY", safeRect.Y },
                    { "Width", safeRect.Width },
                    { "Height", safeRect.Height },
                    { "Angle", 0f }
                }
            }).ToList();
            return result;
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

        private static Rectangle BuildSafeCropRect(int startX, int startY, int width, int height, int sourceWidth, int sourceHeight)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("Width and Height must be positive");
            }

            var requestedRect = new Rectangle(startX, startY, width, height);
            var sourceRect = new Rectangle(0, 0, sourceWidth, sourceHeight);
            var safeRect = Rectangle.Intersect(requestedRect, sourceRect);
            if (safeRect.Width <= 0 || safeRect.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startX), "Crop rectangle must overlap source bounds.");
            }

            return safeRect;
        }

        private static (float[] r, float[] g, float[] b, float[] a, bool sourceHasAlpha) ExtractFloatChannels(IPicture source)
        {
            if (source is IPicture<ushort> p16)
            {
                return (
                    p16.r.Select(Convert.ToSingle).ToArray(),
                    p16.g.Select(Convert.ToSingle).ToArray(),
                    p16.b.Select(Convert.ToSingle).ToArray(),
                    p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray(),
                    p16.hasAlphaChannel && p16.a is not null
                );
            }

            if (source is IPicture<byte> p8)
            {
                return (
                    p8.r.Select(Convert.ToSingle).ToArray(),
                    p8.g.Select(Convert.ToSingle).ToArray(),
                    p8.b.Select(Convert.ToSingle).ToArray(),
                    p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray(),
                    p8.hasAlphaChannel && p8.a is not null
                );
            }

            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
        }

        private static IPicture BuildPicture(IPicture source, int width, int height, float[] r, float[] g, float[] b, float[] a, bool keepAlpha)
        {
            if (source.bitPerPixel == 16)
            {
                var picture = new Picture16bpp(width, height)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.g = g.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.b = b.Select(v => (ushort)Math.Clamp(v, 0f, 65535f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            if (source.bitPerPixel == 8)
            {
                var picture = new Picture8bpp(width, height)
                {
                    frameIndex = source.frameIndex,
                    filePath = source.filePath,
                    hasAlphaChannel = keepAlpha
                };
                picture.r = r.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.g = g.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.b = b.Select(v => (byte)Math.Clamp(v, 0f, 255f)).ToArray();
                picture.a = keepAlpha ? a.Select(v => Math.Clamp(v, 0f, 1f)).ToArray() : null;
                return picture;
            }

            throw new NotSupportedException($"Specific pixel-mode is not supported.");
        }
    }

    public class CropEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "Crop";

        public EffectTarget Target => EffectTarget.Video;

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"Height", "int" },
            {"Width", "int" },
            {"Angle", "float" },
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.HwAcceleration, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }

            return implementType switch
            {
                EffectImplementType.ImageSharp => CropEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.HwAcceleration => CropEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                EffectImplementType.IPicture => CropEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters)
        {
            return CropEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }
    }
}
