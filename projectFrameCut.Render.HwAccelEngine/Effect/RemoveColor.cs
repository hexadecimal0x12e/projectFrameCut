using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using static projectFrameCut.Shared.Logger;
using System.Diagnostics;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class RemoveColorEffect_HwAccel : INormalEffect, IDisposable
    {
        private readonly Lock nativeLock = new();
        private bool disposed;
        public void Dispose()
        {
            using var scope = nativeLock.EnterScope();
            if (disposed)
                return;
            disposed = true;
            ReleaseNativeResources();
        }

        partial void ReleaseNativeResources();
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
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        bool IEffect.CanProcessFromCanvas => true;
        public string? BindedEffectProvidingSystemID { get; set; }
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

            var effect = new RemoveColorEffect_HwAccel
            {
                R = DynamicParam.ToUShort(parameters.GetValueOrDefault("R")),
                G = DynamicParam.ToUShort(parameters.GetValueOrDefault("G")),
                B = DynamicParam.ToUShort(parameters.GetValueOrDefault("B")),
                A = DynamicParam.ToUShort(parameters.GetValueOrDefault("A")),
                Tolerance = DynamicParam.ToUShort(parameters.GetValueOrDefault("Tolerance")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);
        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            try
            {
                using var scope = nativeLock.EnterScope();
                ObjectDisposedException.ThrowIf(disposed, this);
                ushort colorR = DynamicParam.Resolve(Parameters.GetValueOrDefault("R"), R);
                ushort colorG = DynamicParam.Resolve(Parameters.GetValueOrDefault("G"), G);
                ushort colorB = DynamicParam.Resolve(Parameters.GetValueOrDefault("B"), B);
                ushort colorA = DynamicParam.Resolve(Parameters.GetValueOrDefault("A"), A);
                ushort colorTolerance = DynamicParam.Resolve(Parameters.GetValueOrDefault("Tolerance"), Tolerance);
                var sw = Stopwatch.StartNew();
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
                        r[i] = p8.r[i] * 257f;
                        g[i] = p8.g[i] * 257f;
                        b[i] = p8.b[i] * 257f;
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
                alpha = ComputeRemoveColor(r, g, b, a, colorR, colorG, colorB, colorTolerance, source.Pixels);
                if (source is IPicture<ushort> p16_out)
                {
                    var result = new Picture16bpp(p16_out)
                    {
                        r = p16_out.r.ToArray(),
                        g = p16_out.g.ToArray(),
                        b = p16_out.b.ToArray(),
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

                    result.ProcessStack = source.ProcessStack.Concat(new List<PictureProcessStack> { new PictureProcessStack { OperationDisplayName = $"Replace color", Operator = typeof(RemoveColorEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "R", colorR }, { "G", colorG }, { "B", colorB }, { "A", colorA }, { "Tolerance", colorTolerance }, } } }).ToList();
                    return new EffectPictureResizer(ImplementType).ResizePicture(PictureEffectChannels.PreserveHdr(result, source), targetWidth, targetHeight);
                }
                else if (source is IPicture<byte> p8_out)
                {
                    var result = new Picture8bpp(p8_out)
                    {
                        r = p8_out.r.ToArray(),
                        g = p8_out.g.ToArray(),
                        b = p8_out.b.ToArray(),
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
                    result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack { OperationDisplayName = $"Replace color", Operator = typeof(RemoveColorEffect_HwAccel), ProcessingFuncStackTrace = new StackTrace(true), Properties = new Dictionary<string, object> { { "R", colorR }, { "G", colorG }, { "B", colorB }, { "A", colorA }, { "Tolerance", colorTolerance }, }, Elapsed = sw.Elapsed }).ToList();
                    return new EffectPictureResizer(ImplementType).ResizePicture(result, targetWidth, targetHeight);
                }

                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }
            catch (Exception ex)
            {
                Log(ex, "Execute RemoveColor effect", "HwAccelEngine");
                throw;
            }
        }
    }
}
