using projectFrameCut.Render;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    public interface IEffect
    {
        /// <summary>
        /// Indicates which plugin this effect comes from.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Define the type name of the effect. 
        /// </summary>
        public string TypeName { get; }
        /// <summary>
        /// Get how this effect is implemented.
        /// </summary>
        public EffectImplementType ImplementType { get; }
        /// <summary>
        /// Name of this effect. Most for display purpose.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// Parameters of the effect.
        /// </summary>
        public Dictionary<string, object> Parameters { get; }

        /// <summary>
        /// Indicates which parameters are needed for this effect.
        /// </summary>
        /// <remarks>
        /// Default implementation reads static <c>ParametersNeeded</c> from the concrete effect type.
        /// </remarks>
        public List<string> ParametersNeeded => EffectMetadataResolver.GetParametersNeeded(this);

        /// <summary>
        /// Indicates the type of each parameter.
        /// </summary>
        /// <remarks>
        /// Default implementation reads static <c>ParametersType</c> from the concrete effect type.
        /// </remarks>
        public Dictionary<string, string> ParametersType => EffectMetadataResolver.GetParametersType(this);
        /// <summary>
        /// Get or set whether the effect is enabled.
        /// </summary>
        public bool Enabled { get; set; }
        /// <summary>
        /// The index of the effect in the effect stack.
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Indicates whether this effect needs a specific computer with the computer which it's ID is <see cref="NeedComputer"/> to run.
        /// Or be null indicates this effect does not need a specific computer.
        /// </summary>
        [JsonIgnore]
        public string? NeedComputer { get; }
        /// <summary>
        /// Gets a value indicating whether the effect produces a rendered <see cref="IPicture"/> or a un-processed <see cref="IPictureProcessStep"/> to be used in the next step.
        /// </summary>
        [JsonIgnore]
        public bool YieldProcessStep { get; }


        /// <summary>
        /// Get the relative width of the effect.
        /// </summary>
        public int RelativeWidth { get; set; }
        /// <summary>
        /// Get the relative height of the effect.
        /// </summary>
        public int RelativeHeight { get; set; }

        /// <summary>
        /// Create a new effect with the given parameters.
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public IEffect WithParameters(Dictionary<string, object> parameters);

        /// <summary>
        /// Render the effect on the source picture to produce a new picture with the target width and height.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="computer"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns>the processed frame</returns>
        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight);

        /// <summary>
        /// Generate some process step instead of rendering the picture directly.
        /// Throw a <see cref="NotImplementedException"/> if this effect does not support yielding process step.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="computer"></param>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns>the processed frame</returns>
        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight);

        /// <summary>
        /// If you'd like to initialize the effect before use, override it.
        /// </summary>
        public virtual void Initialize()
        {
        }
    }

    internal static class EffectMetadataResolver
    {
        private sealed record CacheEntry(List<string> ParametersNeeded, Dictionary<string, string> ParametersType);

        private static readonly ConcurrentDictionary<Type, CacheEntry> s_cache = new();

        public static List<string> GetParametersNeeded(IEffect effect)
        {
            ArgumentNullException.ThrowIfNull(effect);
            var entry = s_cache.GetOrAdd(effect.GetType(), Resolve);
            return new List<string>(entry.ParametersNeeded);
        }

        public static Dictionary<string, string> GetParametersType(IEffect effect)
        {
            ArgumentNullException.ThrowIfNull(effect);
            var entry = s_cache.GetOrAdd(effect.GetType(), Resolve);
            return new Dictionary<string, string>(entry.ParametersType);
        }

        private static CacheEntry Resolve(Type type)
        {
            var needed = ReadStaticList(type, "ParametersNeeded") ?? new List<string>();
            var types = ReadStaticDictionary(type, "ParametersType") ?? new Dictionary<string, string>();
            return new CacheEntry(needed, types);
        }

        private static List<string>? ReadStaticList(Type type, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is List<string> list) return list;
            if (prop?.GetValue(null) is IEnumerable<string> enumerable) return enumerable.ToList();

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
            if (field?.GetValue(null) is List<string> list2) return list2;
            if (field?.GetValue(null) is IEnumerable<string> enumerable2) return enumerable2.ToList();

            return null;
        }

        private static Dictionary<string, string>? ReadStaticDictionary(Type type, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is Dictionary<string, string> dict) return dict;
            if (prop?.GetValue(null) is IDictionary<string, string> idict) return new Dictionary<string, string>(idict);

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static);
            if (field?.GetValue(null) is Dictionary<string, string> dict2) return dict2;
            if (field?.GetValue(null) is IDictionary<string, string> idict2) return new Dictionary<string, string>(idict2);

            return null;
        }
    }

    public enum EffectImplementType
    {
        NotSpecified,
        IPicture,
        ImageSharp,
        HwAcceleration,
        Custom1,
        Custom2,
        Custom3,
        Custom4,
        Custom5,

    }
}
