using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public struct RgbColor(byte r, byte g, byte b)
    {
        public byte R { get; set; } = r;
        public byte G { get; set; } = g;
        public byte B { get; set; } = b;

        public static readonly RgbColor Green = new(0, 255, 0);
        public static readonly RgbColor Red = new(255, 0, 0);
        public static readonly RgbColor Blue = new(0, 0, 255);
        public static readonly RgbColor White = new(255, 255, 255);
        public static readonly RgbColor Black = new(0, 0, 0);
        public static readonly RgbColor Cyan = new(0, 255, 255);
        public static readonly RgbColor Magenta = new(255, 0, 255);
        public static readonly RgbColor Yellow = new(255, 255, 0);
        public static readonly RgbColor Transparent = new(0, 0, 0);

        public static RgbColor Parse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return Green;

            s = s.Trim();

            if (s.StartsWith('#'))
            {
                var hex = s.AsSpan(1);
                if (hex.Length == 3)
                {
                    byte rr = (byte)(HexToNibble(hex[0]) * 17);
                    byte gg = (byte)(HexToNibble(hex[1]) * 17);
                    byte bb = (byte)(HexToNibble(hex[2]) * 17);
                    return new RgbColor(rr, gg, bb);
                }
                if (hex.Length >= 6)
                {
                    byte rr = (byte)(HexToNibble(hex[0]) << 4 | HexToNibble(hex[1]));
                    byte gg = (byte)(HexToNibble(hex[2]) << 4 | HexToNibble(hex[3]));
                    byte bb = (byte)(HexToNibble(hex[4]) << 4 | HexToNibble(hex[5]));
                    return new RgbColor(rr, gg, bb);
                }
                return Green;
            }

            return s.ToLowerInvariant() switch
            {
                "green" => Green,
                "red" => Red,
                "blue" => Blue,
                "white" => White,
                "black" => Black,
                "cyan" => Cyan,
                "magenta" => Magenta,
                "yellow" => Yellow,
                "transparent" => Transparent,
                _ => TryParseHex(s) ?? Green,
            };
        }

        public override readonly string ToString() => $"#{R:X2}{G:X2}{B:X2}";

        private static byte HexToNibble(char c) => c switch
        {
            >= '0' and <= '9' => (byte)(c - '0'),
            >= 'a' and <= 'f' => (byte)(c - 'a' + 10),
            >= 'A' and <= 'F' => (byte)(c - 'A' + 10),
            _ => 0
        };

        private static RgbColor? TryParseHex(string s)
        {
            if (s.Length == 6 && s.All(c => Uri.IsHexDigit(c)))
            {
                byte rr = Convert.ToByte(s.Substring(0, 2), 16);
                byte gg = Convert.ToByte(s.Substring(2, 2), 16);
                byte bb = Convert.ToByte(s.Substring(4, 2), 16);
                return new RgbColor(rr, gg, bb);
            }
            return null;
        }
    }

    public class SubjectMattingMaskGenerator : IBindableArgumentEffectValueProvider
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? BindedArgumentProviderID { get; set; }

        public RgbColor KeyColor { get; set; } = RgbColor.Green;
        public float Tolerance { get; set; } = 0.1f;

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
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
                try { effect.KeyColor = RgbColor.Parse(parameters["KeyColor"].ToString() ?? "Green"); } catch { }
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
            var width = source.Width;
            var height = source.Height;
            int pixelsCount = width * height;

            var r = new bool[pixelsCount];
            var g = new bool[pixelsCount];
            var b = new bool[pixelsCount];

            byte kr = KeyColor.R;
            byte kg = KeyColor.G;
            byte kb = KeyColor.B;
            float toleranceSq = Tolerance * Tolerance * (255f * 255f * 3f);

            if (source is IPicture<byte> p8)
            {
                for (int i = 0; i < pixelsCount; i++)
                {
                    float dr = p8.r[i] - kr;
                    float dg = p8.g[i] - kg;
                    float db = p8.b[i] - kb;
                    float distSq = dr * dr + dg * dg + db * db;
                    bool isSubject = distSq >= toleranceSq;
                    r[i] = isSubject;
                    g[i] = isSubject;
                    b[i] = isSubject;
                }
            }
            else if (source is IPicture<ushort> p16)
            {
                int u16kr = kr * 257;
                int u16kg = kg * 257;
                int u16kb = kb * 257;
                float toleranceSq16 = Tolerance * Tolerance * (65535f * 65535f * 3f);

                for (int i = 0; i < pixelsCount; i++)
                {
                    float dr = (int)p16.r[i] - u16kr;
                    float dg = (int)p16.g[i] - u16kg;
                    float db = (int)p16.b[i] - u16kb;
                    float distSq = dr * dr + dg * dg + db * db;
                    bool isSubject = distSq >= toleranceSq16;
                    r[i] = isSubject;
                    g[i] = isSubject;
                    b[i] = isSubject;
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }

            return new BitMaskPicture
            {
                r = r,
                g = g,
                b = b,
                Width = width,
                Height = height,
                Pixels = pixelsCount,
                HasAlphaChannel = false,
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

    public class SubjectMattingMaskGeneratorFactory
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

    /// <summary>
    /// The Render-side provider of the SubjectMattingMaskGenerator bindable mask source.
    /// </summary>
    public class SubjectMattingMaskGeneratorProvider : EffectProviderBase
    {
        public SubjectMattingMaskGeneratorProvider()
        {
            Name = "Subject Matting";
            Parameters = new Dictionary<string, object>();
        }

        public override string TypeName => "SubjectMattingMaskGenerator";

        public override EffectType TypeOfEffect => EffectType.BindableEffect;

        public override EffectTarget Target => EffectTarget.ValueProvider | EffectTarget.Video;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("KeyColor", EffectArgumentFieldType.String, "#00FF00"),
                Field("Tolerance", EffectArgumentFieldType.Numeric, "0.1")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return parameters is { Count: > 0 }
                ? [SubjectMattingMaskGenerator.FromParametersDictionary(parameters)]
                : [new SubjectMattingMaskGenerator()];
        }
    }
}
