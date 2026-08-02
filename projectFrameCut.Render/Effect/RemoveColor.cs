using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect
{
    public class RemoveColorEffect_HwAccel : INormalEffect, IDynamicArgumentsEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string Id { get; set; }


        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public ushort A { get; init; }
        public ushort Tolerance { get; init; } = 0;
        public IReadOnlyDictionary<string, Func<object?>>? DynamicProviders { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "R", R },
            { "G", G },
            { "B", B },
            { "A", A },
            { "Tolerance", Tolerance },
        };


        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public string NeedComputer => "RemoveColorComputer";
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;
        public string? BindedEffectGroupID { get; set; }


        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "R", "ushort" },
            { "G", "ushort" },
            { "B", "ushort" },
            { "A", "ushort" },
            { "Tolerance", "ushort" },
        };

        public string TypeName => "RemoveColor";

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


            return new RemoveColorEffect_HwAccel
            {
                R = Convert.ToUInt16(parameters["R"]),
                G = Convert.ToUInt16(parameters["G"]),
                B = Convert.ToUInt16(parameters["B"]),
                A = Convert.ToUInt16(parameters["A"]),
                Tolerance = Convert.ToUInt16(parameters["Tolerance"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            ushort colorR = DynamicParam.Resolve(DynamicProviders, "R", R);
            ushort colorG = DynamicParam.Resolve(DynamicProviders, "G", G);
            ushort colorB = DynamicParam.Resolve(DynamicProviders, "B", B);
            ushort colorA = DynamicParam.Resolve(DynamicProviders, "A", A);
            ushort colorTolerance = DynamicParam.Resolve(DynamicProviders, "Tolerance", Tolerance);
            var sw = Stopwatch.StartNew();
            ArgumentNullException.ThrowIfNull(computer, nameof(computer));
            float[] r, g, b, a;
            if (source is IPicture<ushort> p16)
            {
                r = new float[p16.Pixels];
                g = new float[p16.Pixels];
                b = new float[p16.Pixels];
                for (int i = 0; i < p16.Pixels; i++)
                {
                    r[i] = p16.r[i];
                    g[i] = p16.g[i];
                    b[i] = p16.b[i];
                }
                if (p16.a is null)
                {
                    a = new float[p16.Pixels];
                    Array.Fill(a, 1f);
                }
                else
                {
                    a = p16.a;
                }
            }
            else if (source is IPicture<byte> p8)
            {
                r = new float[p8.Pixels];
                g = new float[p8.Pixels];
                b = new float[p8.Pixels];
                for (int i = 0; i < p8.Pixels; i++)
                {
                    r[i] = p8.r[i];
                    g[i] = p8.g[i];
                    b[i] = p8.b[i];
                }
                if (p8.a is null)
                {
                    a = new float[p8.Pixels];
                    Array.Fill(a, 1f);
                }
                else
                {
                    a = p8.a;
                }
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }

            float[] alpha;
            if (computer is IRemoveColorComputer rcc)
            {
                alpha = rcc.ComputeRemoveColor(r, g, b, a, colorR, colorG, colorB, colorTolerance, source.Pixels);
            }
            else
            {
                var alphaArr = computer.Compute(new object[] {
                    r, g, b, a,
                    (float)colorR, (float)colorG, (float)colorB, (float)colorTolerance, source.Pixels
                });
                if (alphaArr[0] is not float[] alphaOut) throw new InvalidOperationException("The output data from computer is invaild.");
                alpha = alphaOut;
            }

            if (source is IPicture<ushort> p16_out)
            {
                p16_out.SetAlpha(true);
                var result = new Picture16bpp(p16_out)
                {
                    r = p16_out.r,
                    g = p16_out.g,
                    b = p16_out.b,
                    a = alpha,
                    HasAlphaChannel = true
                };
                for (int i = 0; i < result.Pixels; i++)
                {
                    if (result.a[i] == 0)
                    {
                        result.r[i] = 0;
                        result.g[i] = 0;
                        result.b[i] = 0;
                        result.a[i] = 0f;
                    }
                }
                result.ProcessStack = source.ProcessStack.Concat(new List<PictureProcessStack>
                {
                    new PictureProcessStack
                    {
                        OperationDisplayName = $"Replace color",
                        Operator = typeof(RemoveColorEffect_HwAccel),
                        ProcessingFuncStackTrace = new StackTrace(true),
                        Properties = new Dictionary<string, object>
                        {
                            { "R", colorR },
                            { "G", colorG },
                            { "B", colorB },
                            { "A", colorA },
                            { "Tolerance", colorTolerance },
                        }
                    }
                }).ToList();

                return result.Resize(targetWidth, targetHeight, false);
            }
            else if (source is Picture8bpp p8_out)
            {
                p8_out.SetAlpha(true);
                var result = new Picture8bpp(p8_out)
                {
                    r = p8_out.r,
                    g = p8_out.g,
                    b = p8_out.b,
                    a = alpha,
                    HasAlphaChannel = true
                };
                for (int i = 0; i < result.Pixels; i++)
                {
                    if (result.a[i] == 0)
                    {
                        result.r[i] = 0;
                        result.g[i] = 0;
                        result.b[i] = 0;
                        result.a[i] = 0f;
                    }
                }
                sw.Stop();

                result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = $"Replace color",
                    Operator = typeof(RemoveColorEffect_HwAccel),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "R", colorR },
                        { "G", colorG },
                        { "B", colorB },
                        { "A", colorA },
                        { "Tolerance", colorTolerance },
                    },
                    Elapsed = sw.Elapsed
                }).ToList();

                return result.Resize(targetWidth, targetHeight, false);
            }
            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");

        }

    }

    public class RemoveColorEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "RemoveColor";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"R", "ushort" },
            {"G", "ushort" },
            {"B", "ushort" },
            {"A", "ushort" },
            {"Tolerance", "ushort" },
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.HwAcceleration };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            if (implementType != EffectImplementType.HwAcceleration)
            {
                throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.");
            }
            return RemoveColorEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            return RemoveColorEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>());
        }
    }



    /// <summary>
    /// The Render-side provider of the RemoveColor effect.
    /// </summary>
    public class RemoveColorEffectProvider : EffectProviderBase
    {
        public RemoveColorEffectProvider()
        {
            Name = string.Empty;
            Parameters = new Dictionary<string, object>
            {
                { "R", (ushort)0 },
                { "G", (ushort)0 },
                { "B", (ushort)0 },
                { "A", ushort.MaxValue },
                { "Tolerance", (ushort)1200 },
            };
        }

        public override string TypeName => "RemoveColor";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Color", EffectArgumentFieldType.CustomType, """{"r":0,"g":0,"b":0,"a":1.0}""", remarks: "Color to remove (16-bit RGBA)"),
                // The R/G/B/A components are spread from the Color field; declared here so NormalizedParameters
                // knows their param types.
                Field("R", EffectArgumentFieldType.UnsignedInteger, "0"),
                Field("G", EffectArgumentFieldType.UnsignedInteger, "0"),
                Field("B", EffectArgumentFieldType.UnsignedInteger, "0"),
                Field("A", EffectArgumentFieldType.UnsignedInteger, "65535"),
                Field("Tolerance", EffectArgumentFieldType.UnsignedInteger, "1200", min: "0", max: "65535")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.HwAcceleration];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [new RemoveColorEffectFactory().Build(implementType, parameters)];
        }
    }
}
