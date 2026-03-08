using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect
{
    public class RemoveColorEffect_HwAccel : INormalEffect
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
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
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

            var alphaArr = computer.Compute(new object[] {
                r,
                g,
                b,
                a,
                (float)R,
                (float)G,
                (float)B,
                (float)Tolerance,
                source.Pixels
                });

            if (alphaArr[0] is not float[] alpha) throw new InvalidOperationException("The output data from computer is invaild.");

            if (source is IPicture<ushort> p16_out)
            {
                p16_out.SetAlpha(true);
                var result = new Picture(p16_out)
                {
                    r = p16_out.r,
                    g = p16_out.g,
                    b = p16_out.b,
                    a = alpha,
                    hasAlphaChannel = true
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
                            { "R", R },
                            { "G", G },
                            { "B", B },
                            { "A", A },
                            { "Tolerance", Tolerance },
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
                    hasAlphaChannel = true
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
                        { "R", R },
                        { "G", G },
                        { "B", B },
                        { "A", A },
                        { "Tolerance", Tolerance },
                    },
                    Elapsed = sw.Elapsed
                }).ToList();

                return result.Resize(targetWidth, targetHeight, false);
            }
            throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");

        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }
    }

    public class RemoveColorEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "RemoveColor";
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
}
