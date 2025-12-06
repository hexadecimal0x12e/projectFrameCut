using projectFrameCut.Render;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Drawing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.VideoMakeEngine
{
    public class RemoveColorEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }

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
        bool IEffect.NeedAComputer => true;

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

            var alpha = computer.Compute([
                r,
                g,
                b,
                a,
                [(float)R],
                [(float)G],
                [(float)B],
                [(float)Tolerance]
                ])[0];

            

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

        public int StartX { get; init; }
        public int StartY { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"StartX", StartX},
            {"StartY", StartY},
        };

        List<string> IEffect.ParametersNeeded => ParametersNeeded;
        Dictionary<string, string> IEffect.ParametersType => ParametersType;
        bool IEffect.NeedAComputer => false;

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
            if (source is IPicture<ushort> p16)
            {
                Picture result = new Picture(targetWidth, targetHeight)
                {
                    r = new ushort[targetWidth * targetHeight],
                    g = new ushort[targetWidth * targetHeight],
                    b = new ushort[targetWidth * targetHeight],
                    a = new float[targetWidth * targetHeight],
                    hasAlphaChannel = true
                };
                int targetIndex = 0, sourceIndex = 0;
                for (int y = 0; y < source.Height; y++)
                {
                    for (int x = 0; x < source.Width; x++)
                    {
                        if (source.TryFromXYToArrayIndex(x, y, out sourceIndex))
                        {
                            if (result.TryFromXYToArrayIndex(x + StartX, y + StartY, out targetIndex))
                            {
                                result.r[targetIndex] = p16.r[sourceIndex];
                                result.g[targetIndex] = p16.g[sourceIndex];
                                result.b[targetIndex] = p16.b[sourceIndex];
                                result.a[targetIndex] = p16.a != null ? p16.a[sourceIndex] : 1f;
                            }
                            else //use blank to replace content not in range
                            {
                                targetIndex = y * targetWidth + x;
                                result.r[targetIndex] = 0;
                                result.g[targetIndex] = 0;
                                result.b[targetIndex] = 0;
                                result.a[targetIndex] = 0f;
                            }
                        }

                    }

                }
                result.ProcessStack += $"Place to ({StartX},{StartY}) with canvas size {targetWidth}*{targetHeight}\r\n";
                return result;
            }
            else if (source is IPicture<byte> p8)
            {
                Picture8bpp result = new Picture8bpp(targetWidth, targetHeight)
                {
                    r = new byte[targetWidth * targetHeight],
                    g = new byte[targetWidth * targetHeight],
                    b = new byte[targetWidth * targetHeight],
                    a = new float[targetWidth * targetHeight],
                    hasAlphaChannel = true
                };
                int targetIndex = 0, sourceIndex = 0;
                for (int y = 0; y < source.Height; y++)
                {
                    for (int x = 0; x < source.Width; x++)
                    {
                        if (source.TryFromXYToArrayIndex(x, y, out sourceIndex))
                        {
                            if (result.TryFromXYToArrayIndex(x + StartX, y + StartY, out targetIndex))
                            {
                                result.r[targetIndex] = p8.r[sourceIndex];
                                result.g[targetIndex] = p8.g[sourceIndex];
                                result.b[targetIndex] = p8.b[sourceIndex];
                                result.a[targetIndex] = p8.a != null ? p8.a[sourceIndex] : 1f;
                            }
                            else //use blank to replace content not in range
                            {
                                targetIndex = y * targetWidth + x;
                                result.r[targetIndex] = 0;
                                result.g[targetIndex] = 0;
                                result.b[targetIndex] = 0;
                                result.a[targetIndex] = 0f;
                            }
                        }

                    }

                }
                result.ProcessStack += $"Place to ({StartX},{StartY}) with canvas size {targetWidth}*{targetHeight}\r\n";
                return result;
            }
            throw new NotSupportedException();
        }

    }

    public class CropEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }

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
        bool IEffect.NeedAComputer => false;

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

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (source is IPicture<ushort> p16)
            {
                Picture result = new Picture(Width, Height)
                {
                    r = new ushort[Width * Height],
                    g = new ushort[Width * Height],
                    b = new ushort[Width * Height],
                    a = new float[Width * Height],
                    hasAlphaChannel = true
                };
                int targetIndex = 0, sourceIndex = 0;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        if (source.TryFromXYToArrayIndex(x + StartX, y + StartY, out sourceIndex))
                        {
                            if (result.TryFromXYToArrayIndex(x, y, out targetIndex))
                            {
                                result.r[targetIndex] = p16.r[sourceIndex];
                                result.g[targetIndex] = p16.g[sourceIndex];
                                result.b[targetIndex] = p16.b[sourceIndex];
                                result.a[targetIndex] = p16.a != null ? p16.a[sourceIndex] : 1f;
                            }
                        }
                    }
                }
                result.ProcessStack += $"Crop from ({StartX},{StartY}) with size {Width}*{Height}, with canvas size {targetWidth}*{targetHeight}\r\n";

                return result;
            }
            else if (source is IPicture<byte> p8)
            {
                Picture8bpp result = new Picture8bpp(Width, Height)
                {
                    r = new byte[Width * Height],
                    g = new byte[Width * Height],
                    b = new byte[Width * Height],
                    a = new float[Width * Height],
                    hasAlphaChannel = true
                };
                int targetIndex = 0, sourceIndex = 0;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        if (source.TryFromXYToArrayIndex(x + StartX, y + StartY, out sourceIndex))
                        {
                            if (result.TryFromXYToArrayIndex(x, y, out targetIndex))
                            {
                                result.r[targetIndex] = p8.r[sourceIndex];
                                result.g[targetIndex] = p8.g[sourceIndex];
                                result.b[targetIndex] = p8.b[sourceIndex];
                                result.a[targetIndex] = p8.a != null ? p8.a[sourceIndex] : 1f;
                            }
                        }
                    }
                }
                result.ProcessStack += $"Crop from ({StartX},{StartY}) with size {Width}*{Height} with canvas size {targetWidth}*{targetHeight}\r\n";

                return result;
            }
            throw new NotSupportedException();
        }


    }

    public class ResizeEffect : IEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }

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
        bool IEffect.NeedAComputer => false;

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
            => source.Resize(Width, Height, PreserveAspectRatio);


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
