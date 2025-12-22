using projectFrameCut.Render;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Drawing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Render.VideoMakeEngine
{
    public class RemoveColorEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


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

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public string NeedComputer => "RemoveColorComputer";


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


            return new RemoveColorEffect
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
            ArgumentNullException.ThrowIfNull(computer, nameof(computer));

            float[] r, g, b, a;
            if (source is IPicture<ushort> p16)
            {
                r = p16.r.Select(Convert.ToSingle).ToArray();
                g = p16.g.Select(Convert.ToSingle).ToArray();
                b = p16.b.Select(Convert.ToSingle).ToArray();
                a = p16.a ?? Enumerable.Repeat(1f, p16.Pixels).ToArray();
            }
            else if (source is IPicture<byte> p8)
            {
                r = p8.r.Select(Convert.ToSingle).ToArray();
                g = p8.g.Select(Convert.ToSingle).ToArray();
                b = p8.b.Select(Convert.ToSingle).ToArray();
                a = p8.a ?? Enumerable.Repeat(1f, p8.Pixels).ToArray();
            }
            else
            {
                throw new NotSupportedException($"Unsupported picture type: {source.GetType().Name}");
            }

            var alphaArr = computer.Compute([
                r,
                g,
                b,
                a,
                (float)R,
                (float)G,
                (float)B,
                (float)Tolerance
                ]);

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
                result.ProcessStack += $"Replace color #{R:x2}{G:x2}{B:x2} tol:{Tolerance}\r\n";

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
                result.ProcessStack += $"Replace color #{R:x2}{G:x2}{B:x2} tol:{Tolerance}\r\n";

                return result.Resize(targetWidth, targetHeight, false);
            }
            throw new NotSupportedException();
        }
    }

    public class PlaceEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public int StartX { get; init; }
        public int StartY { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"StartX", StartX},
            {"StartY", StartY},
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string? NeedComputer => null;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY"
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX","int" },
            {"StartY","int" },
        };

        public string TypeName => "Place";

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


            return new PlaceEffect
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            int startX = StartX;
            int startY = StartY;

            if (RelativeWidth > 0 && RelativeHeight > 0)
            {
                startX = (int)((long)StartX * targetWidth / RelativeWidth);
                startY = (int)((long)StartY * targetHeight / RelativeHeight);
            }

            var pixelMapping = ComputePixelMapping(
                source.Width, source.Height, targetWidth, targetHeight, startX, startY);

            if (source is IPicture<ushort> p16)
            {
                p16.a ??= Enumerable.Repeat(1f, p16.Pixels).ToArray();

                Picture result = new Picture(targetWidth, targetHeight)
                {
                    r = new ushort[targetWidth * targetHeight],
                    g = new ushort[targetWidth * targetHeight],
                    b = new ushort[targetWidth * targetHeight],
                    a = new float[targetWidth * targetHeight],
                    hasAlphaChannel = true
                };

                foreach (var mapping in pixelMapping)
                {
                    int sourceIndex = mapping.Key;
                    int targetIndex = mapping.Value;

                    result.r[targetIndex] = p16.r[sourceIndex];
                    result.g[targetIndex] = p16.g[sourceIndex];
                    result.b[targetIndex] = p16.b[sourceIndex];
                    result.a[targetIndex] = p16.a[sourceIndex];
                }

                result.ProcessStack += $"Place to ({StartX},{StartY}) with canvas size {targetWidth}*{targetHeight}\r\n";
                return result;
            }
            else if (source is IPicture<byte> p8)
            {
                p8.a ??= Enumerable.Repeat(1f, p8.Pixels).ToArray();

                Picture8bpp result = new Picture8bpp(targetWidth, targetHeight)
                {
                    r = new byte[targetWidth * targetHeight],
                    g = new byte[targetWidth * targetHeight],
                    b = new byte[targetWidth * targetHeight],
                    a = new float[targetWidth * targetHeight],
                    hasAlphaChannel = true
                };

                foreach (var mapping in pixelMapping)
                {
                    int sourceIndex = mapping.Key;
                    int targetIndex = mapping.Value;

                    result.r[targetIndex] = p8.r[sourceIndex];
                    result.g[targetIndex] = p8.g[sourceIndex];
                    result.b[targetIndex] = p8.b[sourceIndex];
                    result.a[targetIndex] = p8.a[sourceIndex];
                }

                result.ProcessStack += $"Place to ({StartX},{StartY}) with canvas size {targetWidth}*{targetHeight}\r\n";
                return result;
            }
            throw new NotSupportedException();
        }


        public static Dictionary<int, int> ComputePixelMapping(int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight, int startX, int startY)
        {
            var mapping = new Dictionary<int, int>();

            for (int y = 0; y < sourceHeight; y++)
            {
                for (int x = 0; x < sourceWidth; x++)
                {
                    int newX = x + startX;
                    int newY = y + startY;

                    if (newX >= targetWidth || newY >= targetHeight)
                        continue;

                    if (newX >= 0 && newY >= 0)
                    {
                        int sourceIndex = y * sourceWidth + x;
                        int targetIndex = newY * targetWidth + newX;
                        mapping[sourceIndex] = targetIndex;
                    }
                }
            }

            return mapping;
        }
    }

    public class CropEffect : IEffect
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

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"StartX", StartX },
            {"StartY", StartY },
            {"Height", Height },
            {"Width", Width },
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string? NeedComputer => null;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "Height",
            "Width",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"Height", "int" },
            {"Width", "int" },
        };

        public string TypeName => "Crop";

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


            return new CropEffect
            {
                StartX = Convert.ToInt32(parameters["StartX"]),
                StartY = Convert.ToInt32(parameters["StartY"]),
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        [DebuggerStepThrough()]
        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            int startX = StartX;
            int startY = StartY;
            int width = Width;
            int height = Height;

            if (RelativeWidth > 0 && RelativeHeight > 0)
            {
                startX = (int)((long)StartX * targetWidth / RelativeWidth);
                startY = (int)((long)StartY * targetHeight / RelativeHeight);
                width = (int)((long)Width * targetWidth / RelativeWidth);
                height = (int)((long)Height * targetHeight / RelativeHeight);
            }

            // 使用 ManagedCropComputer 计算像素映射
            var pixelMapping = ComputePixelMapping(
                source.Width, source.Height, width, height, startX, startY);

            if (source is IPicture<ushort> p16)
            {
                p16.a ??= Enumerable.Repeat(1f, p16.Pixels).ToArray();

                Picture result = new Picture(width, height)
                {
                    r = new ushort[width * height],
                    g = new ushort[width * height],
                    b = new ushort[width * height],
                    a = new float[width * height],
                    hasAlphaChannel = true
                };

                // 按照像素映射复制像素
                foreach (var mapping in pixelMapping)
                {
                    int sourceIndex = mapping.Key;
                    int targetIndex = mapping.Value;

                    result.r[targetIndex] = p16.r[sourceIndex];
                    result.g[targetIndex] = p16.g[sourceIndex];
                    result.b[targetIndex] = p16.b[sourceIndex];
                    result.a[targetIndex] = p16.a[sourceIndex];
                }

                result.ProcessStack += $"Crop from ({StartX},{StartY}) with size {Width}*{Height}, with canvas size {targetWidth}*{targetHeight}\r\n";
                return result;
            }
            else if (source is IPicture<byte> p8)
            {
                Picture8bpp result = new Picture8bpp(width, height)
                {
                    r = new byte[width * height],
                    g = new byte[width * height],
                    b = new byte[width * height],
                    a = new float[width * height],
                    hasAlphaChannel = true
                };

                p8.a ??= Enumerable.Repeat(1f, p8.Pixels).ToArray();

                foreach (var mapping in pixelMapping)
                {
                    int sourceIndex = mapping.Key;
                    int targetIndex = mapping.Value;

                    result.r[targetIndex] = p8.r[sourceIndex];
                    result.g[targetIndex] = p8.g[sourceIndex];
                    result.b[targetIndex] = p8.b[sourceIndex];
                    result.a[targetIndex] = p8.a[sourceIndex];
                }

                result.ProcessStack += $"Crop from ({StartX},{StartY}) with size {Width}*{Height} with canvas size {targetWidth}*{targetHeight}\r\n";
                return result;
            }
            throw new NotSupportedException();
        }


        public static Dictionary<int, int> ComputePixelMapping(int sourceWidth, int sourceHeight,
            int cropWidth, int cropHeight, int startX, int startY)
        {
            var mapping = new Dictionary<int, int>();

            for (int y = 0; y < cropHeight; y++)
            {
                for (int x = 0; x < cropWidth; x++)
                {
                    int sourceX = x + startX;
                    int sourceY = y + startY;

                    if (sourceX < 0 || sourceY < 0 || sourceX >= sourceWidth || sourceY >= sourceHeight)
                        continue;

                    int sourceIndex = sourceY * sourceWidth + sourceX;
                    int targetIndex = y * cropWidth + x;
                    mapping[sourceIndex] = targetIndex;
                }
            }

            return mapping;
        }

    }

    public class ResizeEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; }
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }


        public int Height { get; init; }
        public int Width { get; init; }
        public bool PreserveAspectRatio { get; init; } = true;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"Height", Height },
            {"Width", Width },
            {"PreserveAspectRatio" , PreserveAspectRatio  },
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public string? NeedComputer => null;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Height",
            "Width",
            "PreserveAspectRatio"
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Height", "int" },
            {"Width", "int" },
            {"PreserveAspectRatio", "bool" },
        };

        public string TypeName => "Resize";

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


            return new ResizeEffect
            {
                Height = Convert.ToInt32(parameters["Height"]),
                Width = Convert.ToInt32(parameters["Width"]),
                PreserveAspectRatio = Convert.ToBoolean(parameters["PreserveAspectRatio"]),
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            int width = Width;
            int height = Height;

            if (RelativeWidth > 0 && RelativeHeight > 0)
            {
                width = (int)((long)Width * targetWidth / RelativeWidth);
                height = (int)((long)Height * targetHeight / RelativeHeight);
            }
            return source.Resize(width, height, PreserveAspectRatio);
        }


    }

    /*
    public class EffectBase : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"", },
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"","" },
        };

        public string TypeName => "";
        public static string s_TypeName => "";

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


            return new EffectBase
            {
                
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public Picture Render(Picture source, IComputer computer, int targetWidth, int targetHeight)
        {
            
        }
    }
    */

}
