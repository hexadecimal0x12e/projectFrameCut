using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Color = SixLabors.ImageSharp.Color;

namespace projectFrameCut.Render.Effect
{

    #region Point Placer

    public class PointPlacer : IBindableArgumentEffectOneInputResultGenerator
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string TypeName => "PointPlacer";
        public string? BindedArgumentProviderID { get; set; }

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType => EffectImplementType.ImageSharp;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>();

        public static List<string> ParametersNeeded { get; } = new List<string>();
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>();

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters) => new PointPlacer();

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (source is not Func<double, System.Drawing.Point> func) throw new ArgumentException("Source is not a valid callback function.", nameof(source));
            var prog = EffectHelper.GetContinuesEffectProgress(index, StartPoint, EndPoint);
            var pt = func.Invoke(prog);
            LogDiagnostic($"[PointPlacer,{Id}] Placing to {pt} in {prog}...");
            var x = pt.X;
            var y = pt.Y;

            int startX = x, startY = y;
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)startX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)startY * targetHeight / RelativeHeight);
            }

            var step = new PlaceProcessStep(startX, startY, targetWidth, targetHeight);
            return step.Process(frame);
        }

        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight)
        {
            if (source is not Func<double, System.Drawing.Point> func) throw new ArgumentException("Source is not a valid callback function.", nameof(source));
            var prog = EffectHelper.GetContinuesEffectProgress(index, StartPoint, EndPoint);
            var pt = func.Invoke(prog);
            LogDiagnostic($"[PointPlacer,{Id}] Placing to {pt} in {prog}...");
            var x = pt.X;
            var y = pt.Y;
            int startX = x, startY = y;
            if (RelativeWidth > 0 && RelativeHeight > 0 && (RelativeWidth != targetWidth || RelativeHeight != targetHeight))
            {
                startX = (int)Math.Round((double)startX * targetWidth / RelativeWidth);
                startY = (int)Math.Round((double)startY * targetHeight / RelativeHeight);
            }

            return new PlaceProcessStep(startX, startY, targetWidth, targetHeight);
        }


        public bool IsValueValid(object value)
        {
            return value is Func<double, System.Drawing.Point>;
        }

        public void Initialize() { }

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string InputAnchorName => "Input";

        public bool IsContinuous => true;

        public string OutputAnchorName => "Point";
    }

    public class PointPlacerFactory : IBindableEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "PointPlacer";
        public List<string> ParametersNeeded => PointPlacer.ParametersNeeded;
        public Dictionary<string, string> ParametersType => PointPlacer.ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp };


        public string? ID { get; set; }
        public string? BindedInputID { get; set; }
        public string[]? BindedInputIDs { get; set; }

        public IEffect BuildWithDefaultType(string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return Build(SupportsImplementTypes[0], ID, BindedInputID, BindedInputIDs, parameters);
        }

        public IEffect Build(EffectImplementType implementType, string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            if (implementType != EffectImplementType.NotSpecified && !SupportsImplementTypes.Contains(implementType))
            {
                throw new ArgumentException($"ImplementType {implementType} is not supported.", nameof(implementType));
            }

            var e = parameters != null ? PointPlacer.FromParametersDictionary(parameters) : new PointPlacer();

            if (e is IBindableArgumentEffect be)
            {
                be.Id = Guid.NewGuid().ToString();

                if (BindedInputID != null)
                {
                    be.BindedArgumentProviderID = BindedInputID;
                }
                else if (parameters != null)
                {
                    throw new InvalidDataException("Invaild source ID.");
                }
            }
            return e;
        }
    }

    public class StraightLineMovementValueProducer : IBindableArgumentEffectValueProvider
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? BindedArgumentProviderID { get; set; }

        public int StartX { get; set; }
        public int StartY { get; set; }
        public int EndX { get; set; }
        public int EndY { get; set; }

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public string TypeName => "StraightLineMovementValueProducer";

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
            { "EndX", EndX },
            { "EndY", EndY },
        };

        public static List<string> ParametersNeeded { get; } = StraightLineMovementValueProducerFactory.s_ParametersNeeded;
        public static Dictionary<string, string> ParametersType { get; } = StraightLineMovementValueProducerFactory.s_ParametersType;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            var effect = new StraightLineMovementValueProducer();
            if (parameters.TryGetValue("StartX", out var startX)) effect.StartX = Convert.ToInt32(startX);
            if (parameters.TryGetValue("StartY", out var startY)) effect.StartY = Convert.ToInt32(startY);
            if (parameters.TryGetValue("EndX", out var endX)) effect.EndX = Convert.ToInt32(endX);
            if (parameters.TryGetValue("EndY", out var endY)) effect.EndY = Convert.ToInt32(endY);
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return new Func<double, System.Drawing.Point>((progress) =>
            {
                int x = (int)Math.Round(StartX + (EndX - StartX) * progress);
                int y = (int)Math.Round(StartY + (EndY - StartY) * progress);
                return new System.Drawing.Point(x, y);
            });
        }

        public bool IsValueValid(object value) => value is Func<double, System.Drawing.Point>;

        public void Initialize() { }

        public bool GenerateOnce => true;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string OutputAnchorName => "Point";
    }

    public class StraightLineMovementValueProducerFactory : IBindableEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "StraightLineMovementValueProducer";
        public static List<string> s_ParametersNeeded = new List<string>
        {
            "StartX",
            "StartY",
            "EndX",
            "EndY",
        };

        public static Dictionary<string, string> s_ParametersType = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"EndX", "int" },
            {"EndY", "int" },
        };
        public List<string> ParametersNeeded => s_ParametersNeeded;
        public Dictionary<string, string> ParametersType => s_ParametersType;

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.NotSpecified };


        public string? ID { get; set; }
        public string? BindedInputID { get; set; }
        public string[]? BindedInputIDs { get; set; }

        public IEffect BuildWithDefaultType(string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            return Build(SupportsImplementTypes[0], ID, BindedInputID, BindedInputIDs, parameters);
        }

        public IEffect Build(EffectImplementType implementType, string? ID, string? BindedInputID, string[]? BindedInputIDs = null, Dictionary<string, object>? parameters = null)
        {
            if (implementType != EffectImplementType.NotSpecified && !SupportsImplementTypes.Contains(implementType))
            {
                throw new ArgumentException($"ImplementType {implementType} is not supported.", nameof(implementType));
            }

            var e = parameters != null ? StraightLineMovementValueProducer.FromParametersDictionary(parameters) : new StraightLineMovementValueProducer();

            if (e is IBindableArgumentEffect be)
            {
                if (ID != null)
                {
                    be.Id = ID;
                }
                else if (parameters != null)
                {
                    throw new InvalidDataException("Invaild source ID.");
                }
                be.BindedArgumentProviderID = null!;
            }
            return e;
        }
    }





    #endregion

    #region subject match
    public class SubjectMattingMaskGenerator : IBindableArgumentEffectValueProvider
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

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

        public bool IsValueValid(object value) => true;

        public void Initialize() { }

        public bool GenerateOnce => false;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string OutputAnchorName => "Mask";
    }

    public class MaskApplier : IBindableArgumentEffectOneInputResultGenerator
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

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


        /// <summary>
        /// Applies the mask to the frame (ResultGenerator role).
        /// </summary>
        public IPicture GenerateResult(object source, uint index, IPicture frame, IComputer? computer, int targetWidth, int targetHeight)
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


        public bool IsValueValid(object value) => value is BitMaskPicture;



        public IPictureProcessStep GenerateResultStep(object source, uint index, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public string InputAnchorName => "Mask";

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public bool IsContinuous => false;

        public string OutputAnchorName => "Mask";
    }

    #endregion


    #region subject match factory

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

    #endregion
}