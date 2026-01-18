using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class SubjectMattingMaskGenerator : IBindableArgumentEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public BindableArgumentEffectType EffectRole { get; set; } = BindableArgumentEffectType.ValueProvider;
        public string? BindedArgumentProviderID { get; set; }

        // Parameters
        public Color KeyColor { get; set; } = Color.Green;
        public float Tolerance { get; set; } = 0.1f; // 0.0 - 1.0

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public string TypeName => "SubjectMattingMaskGenerator";

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "KeyColor", KeyColor.ToString() },
            { "Tolerance", Tolerance }
        };

        public static List<string> ParametersNeeded { get; } = new List<string> { "KeyColor", "Tolerance" };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "KeyColor", "Color" },
            { "Tolerance", "float" }
        };

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            var effect = new SubjectMattingMaskGenerator();
            if (parameters.ContainsKey("KeyColor"))
            {
                try { effect.KeyColor = Color.Parse(parameters["KeyColor"].ToString() ?? "Green"); } catch { }
            }
            if (parameters.ContainsKey("Tolerance"))
            {
                effect.Tolerance = Convert.ToSingle(parameters["Tolerance"]);
            }
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        /// <summary>
        /// Generates a mask (ValueProvider role).
        /// </summary>
        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            // Convert source to Rgba32 for pixel access
            using var srcImg = source.SaveToSixLaborsImage().CloneAs<Rgba32>();

            var width = srcImg.Width;
            var height = srcImg.Height;
            int pixelsCount = width * height;

            var r = new bool[pixelsCount];
            var g = new bool[pixelsCount];
            var b = new bool[pixelsCount];

            var keyColorRgba = KeyColor.ToPixel<Rgba32>();
            float toleranceSq = Tolerance * Tolerance * (255 * 255 * 3); // Approx max distance squared

            srcImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int rowOffset = y * width;

                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];

                        // Calculate Euclidean distance squared
                        float distSq = (pixel.R - keyColorRgba.R) * (pixel.R - keyColorRgba.R) +
                                       (pixel.G - keyColorRgba.G) * (pixel.G - keyColorRgba.G) +
                                       (pixel.B - keyColorRgba.B) * (pixel.B - keyColorRgba.B);

                        bool isSubject;
                        if (distSq < toleranceSq)
                        {
                            // It's the key color (Background) -> False
                            isSubject = false;
                        }
                        else
                        {
                            // It's the subject -> True
                            isSubject = true;
                        }

                        int index = rowOffset + x;
                        r[index] = isSubject;
                        g[index] = isSubject;
                        b[index] = isSubject;
                    }
                }
            });

            return new BitMaskPicture
            {
                r = r,
                g = g,
                b = b,
                Width = width,
                Height = height,
                Pixels = pixelsCount,
                hasAlphaChannel = false,
                ProcessStack = source.ProcessStack?.ToList() ?? new List<PictureProcessStack>()
            };
        }

        public object ProcessValue(object source, IComputer? computer, int targetWidth, int targetHeight) => source;

        public IPicture GenerateResult(object source, IPicture frame, IComputer? computer, int targetWidth, int targetHeight) 
            => throw new NotSupportedException("This effect is a ValueProvider, not a ResultGenerator.");

        public IPictureProcessStep GenerateResultStep(object source, int targetWidth, int targetHeight) 
            => throw new NotSupportedException("This effect is a ValueProvider, not a ResultGenerator.");

        public bool IsValueValid(object value) => true;

        public void Initialize() { }

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

    }

    public class MaskApplier : IBindableArgumentEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public BindableArgumentEffectType EffectRole { get; set; } = BindableArgumentEffectType.ResultGenerator;
        public string? BindedArgumentProviderID { get; set; }

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;
        public string TypeName => "MaskApplier";

        public Dictionary<string, object> Parameters => new Dictionary<string, object>();

        public static List<string> ParametersNeeded { get; } = new List<string>();
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>();

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters) => new MaskApplier();

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
             => throw new NotSupportedException("This effect is a ResultGenerator, not a ValueProvider.");

        public object ProcessValue(object source, IComputer? computer, int targetWidth, int targetHeight) => source;

        /// <summary>
        /// Applies the mask to the frame (ResultGenerator role).
        /// </summary>
        public IPicture GenerateResult(object source, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (source is not BitMaskPicture maskPic)
            {
                // If no mask provided, return original frame or throw
                return frame;
            }

            var frameImg = frame.SaveToSixLaborsImage().CloneAs<Rgba32>();

            bool sizeMatch = maskPic.Width == frameImg.Width && maskPic.Height == frameImg.Height;

            frameImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int maskRowOffset = y * maskPic.Width; // Assuming mask matches frame width if sizeMatch

                    for (int x = 0; x < row.Length; x++)
                    {
                        bool keepPixel;
                        if (sizeMatch)
                        {
                            // Direct mapping
                            // Using Red channel of mask as the primary mask value
                            keepPixel = maskPic.r[maskRowOffset + x];
                        }
                        else
                        {
                            // Nearest neighbor sampling for mismatched sizes
                            int maskX = (int)((float)x / frameImg.Width * maskPic.Width);
                            int maskY = (int)((float)y / frameImg.Height * maskPic.Height);
                            int maskIndex = maskY * maskPic.Width + maskX;
                            if (maskIndex < maskPic.r.Length)
                                keepPixel = maskPic.r[maskIndex];
                            else
                                keepPixel = true; // Fallback
                        }

                        if (!keepPixel)
                        {
                            // Background -> Transparent
                            row[x] = new Rgba32(0, 0, 0, 0);
                        }
                        // Else keep original pixel
                    }
                }
            });

            return new Picture8bpp(frameImg);
        }

        public IPictureProcessStep GenerateResultStep(object source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

        public bool IsValueValid(object value) => value is BitMaskPicture;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


    }

    public class SubjectMattingMaskGeneratorFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "SubjectMattingMaskGenerator";
        public List<string> ParametersNeeded => SubjectMattingMaskGenerator.ParametersNeeded;
        public Dictionary<string, string> ParametersType => SubjectMattingMaskGenerator.ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.NotSpecified };

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return Build(SupportsImplementTypes[0], parameters);
        }

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (!SupportsImplementTypes.Contains(implementType))
            {
                throw new ArgumentException($"ImplementType {implementType} is not supported.", nameof(implementType));
            }

            if (parameters != null)
            {
                return SubjectMattingMaskGenerator.FromParametersDictionary(parameters);
            }
            return new SubjectMattingMaskGenerator();
        }
    }

    public class MaskApplierFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "MaskApplier";
        public List<string> ParametersNeeded => MaskApplier.ParametersNeeded;
        public Dictionary<string, string> ParametersType => MaskApplier.ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp };

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return Build(SupportsImplementTypes[0], parameters);
        }

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (!SupportsImplementTypes.Contains(implementType))
            {
                throw new ArgumentException($"ImplementType {implementType} is not supported.", nameof(implementType));
            }

            if (parameters != null)
            {
                return MaskApplier.FromParametersDictionary(parameters);
            }
            return new MaskApplier();
        }
    }
}