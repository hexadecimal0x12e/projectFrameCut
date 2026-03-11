//most classes in this file are used for saving and reading draft/effect/clip, so you probably not excepted to use these classes while you're making plugin.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// Provide a unified way for calculate something with GPU.
    /// </summary>
    public interface IComputer
    {
        /// <summary>
        /// Indicates which plugin this computer comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Represents the effect or mixture type name that this computer supports.
        /// </summary>
        public string SupportedEffectOrMixture { get; }
        /// <summary>
        /// Compute the output based on the input arguments.
        /// The <paramref name="args"/> can be any forms you'd like. No any limitations,
        /// so please make sure to provide enough args in correct order for the computer to compute the result.
        /// </summary>
        /// <param name="args">Input data</param>
        /// <returns>output data</returns>
        public object[] Compute(object[] args);
    }

    public interface IEffectArgsEnumHandler
    {
        public int Parse(string value);
        public string FromEnum(int value);
        public Dictionary<int,string> Mapping { get; }
    }

    public class EffectAndMixtureJSONStructure
    {
        public string BindedEffectGroupID { get; set; } = string.Empty;
        public bool IsMixture { get; set; } = false;
        public bool IsContinuousEffect { get; set; } = false;
        public bool IsVariableArgumentEffect { get; set; } = false;
        public string FromPlugin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public int Index { get; set; } = 1;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public Dictionary<string, object>? Parameters { get; set; }
        public string? Id { get; set; }
        public string? BindedInputID { get; set; } = null;
        public string[]? BindedInputIDs { get; set; } = null;
    }

    public class EffectBundleJSONStructure
    {
        private static readonly Guid NoConnectionGuid = new("00001234-5678-90ab-cdef-012345678900");

        public Guid Id { get; set; } = Guid.NewGuid();
        public string FromPlugin { get; set; } = string.Empty;
        public string BundleTypeName { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new();
        public string Name { get; set; } = string.Empty;
        public Guid BindedInputId { get; set; } = NoConnectionGuid;
        public Guid BindedOutputId { get; set; } = NoConnectionGuid;
        public Guid[]? BindedInputIds { get; set; }
        public double InteractiveEditorX { get; set; } = -1;
        public double InteractiveEditorY { get; set; } = -1;
    }

    public static class EffectArgsHelper
    {
        static string GetParamType(string type, Dictionary<string, string> ParametersType)
        {
            if (ParametersType.TryGetValue(type, out var t)) return t;
            return type switch
            {
                "__DraftEffectBindingView_InteractiveEditorX__" => "double",
                "__DraftEffectBindingView_InteractiveEditorY__" => "double",
                _ => throw new NotImplementedException($"Parameter '{type}' has an undefined type."),

            };
        }

        /// <summary>
        /// Convert a dictionary with JsonElement values to a dictionary with object values according to the given parameter types.
        /// </summary>
        /// <param name="elements"></param>
        /// <param name="ParametersType"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public static Dictionary<string, object> ConvertElementDictToObjectDict(Dictionary<string, object> elements, Dictionary<string, string> ParametersType, IEffectArgsEnumHandler? EnumHandler = null)
        {
            var result = new Dictionary<string, object>();
            
            foreach (var kvp in elements)
            {
                if (kvp.Value is not JsonElement)
                {
                    object obj = GetParamType(kvp.Key, ParametersType) switch
                    {
                        "ushort" => Convert.ToUInt16(kvp.Value),
                        "int" => Convert.ToInt32(kvp.Value),
                        "float" => Convert.ToSingle(kvp.Value),
                        "double" => Convert.ToDouble(kvp.Value),
                        "string" => Convert.ToString(kvp.Value)!,
                        "bool" => Convert.ToBoolean(kvp.Value),
                        "long" => Convert.ToInt64(kvp.Value),
                        "enum" => EnumHandler is not null ? EnumHandler.Parse(Convert.ToString(kvp.Value)!) : throw new NotSupportedException($"Source is enum but no handler provided."),
                        _ => throw new NotImplementedException($"Parameter type '{ParametersType[kvp.Key]}' is not implemented."),
                    };
                    result.Add(kvp.Key, obj);
                }
                else
                {
                    object value = null;
                    JsonElement source = (JsonElement)kvp.Value;
                    switch (GetParamType(kvp.Key, ParametersType))
                    {
                        case "ushort":
                            value = source.GetUInt16();
                            break;
                        case "int":
                            value = source.GetInt32();
                            break;
                        case "float":
                            value = source.GetSingle();
                            break;
                        case "double":
                            value = source.GetDouble();
                            break;
                        case "string":
                            value = source.GetString()!;
                            break;
                        case "bool":
                            value = source.GetBoolean();
                            break;
                        case "long":
                            value = source.GetInt64();
                            break;
                        case "enum":
                            if(EnumHandler is not null)
                            {
                                value = EnumHandler.Parse(source.GetString());
                                break;
                            }
                            throw new NotSupportedException($"Source is enum but no handler provided.");
                        default:
                            throw new NotImplementedException($"Parameter type '{ParametersType[kvp.Key]}' is not implemented.");
                    }
                    result.Add(kvp.Key, value);
                }
            }
            return result;
        }

        public static string ArgTypeString => "string";
        public static string ArgTypeUInt16 => "ushort";
        public static string ArgTypeInt32 => "int";
        public static string ArgTypeInt64 => "long";
        public static string ArgTypeDouble => "double";
        public static string ArgTypeFloat => "float";
        public static string ArgTypeBool => "bool";
        public static string ArgTypeEnum => "enum";
    }
}
