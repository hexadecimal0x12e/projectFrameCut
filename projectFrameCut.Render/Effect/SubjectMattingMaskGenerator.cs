using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class SubjectMattingMaskGenerator : IBindableArgumentEffectValueProvider
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? BindedArgumentProviderID { get; set; }

        public Color KeyColor { get; set; } = Color.Green;
        public float Tolerance { get; set; } = 0.1f;

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

        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            using var srcImg = source.SaveToSixLaborsImage().CloneAs<Rgba32>();

            var width = srcImg.Width;
            var height = srcImg.Height;
            int pixelsCount = width * height;

            var r = new bool[pixelsCount];
            var g = new bool[pixelsCount];
            var b = new bool[pixelsCount];

            var keyColorRgba = KeyColor.ToPixel<Rgba32>();
            float toleranceSq = Tolerance * Tolerance * (255 * 255 * 3);

            srcImg.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    int rowOffset = y * width;

                    for (int x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];

                        float distSq = (pixel.R - keyColorRgba.R) * (pixel.R - keyColorRgba.R) +
                                       (pixel.G - keyColorRgba.G) * (pixel.G - keyColorRgba.G) +
                                       (pixel.B - keyColorRgba.B) * (pixel.B - keyColorRgba.B);

                        bool isSubject = distSq >= toleranceSq;

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

        public bool IsValueValid(object value) => true;

        public void Initialize() { }

        public bool GenerateOnce => false;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string OutputAnchorName => "Mask";
    }

    public class SubjectMattingMaskGeneratorFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "SubjectMattingMaskGenerator";
        public EffectTarget Target => EffectTarget.Video;
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
}
