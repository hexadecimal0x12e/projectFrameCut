using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.RenderAPIBase.Plugins
{
    /// <summary>
    /// A root structure for all extensible objects in the plugin system.
    /// </summary>
    public interface IExtensibleObject
    {
        /// <summary>
        /// Gets the ID of the plugin that provided this object.
        /// </summary>
        public virtual string FromPlugin => GetType().Assembly.GetName().Name ?? string.Empty;

        /// <summary>
        /// Define the type name of the object. 
        /// </summary>
        public virtual string TypeName => GetType().Name;

        /// <summary>
        /// Indicate a unique identifier for this specific instance of the extensible object.
        /// </summary>
        public virtual Guid Id
        {
            get => ExtensibleObjectState.GetId(this);
            set => ExtensibleObjectState.SetId(this, value);
        }

        /// <summary>
        /// If you'd like to initialize this object before use, override it.
        /// </summary>
        public virtual void Initialize()
        {
        }
    }

    internal static class ExtensibleObjectState
    {
        private sealed class State
        {
            public Guid Id { get; set; } = Guid.NewGuid();
        }

        private static readonly ConditionalWeakTable<object, State> states = new();

        public static Guid GetId(IExtensibleObject value)
        {
            if (states.TryGetValue(value, out var state)) return state.Id;
            if (value.GetType().GetProperty(nameof(IExtensibleObject.Id), BindingFlags.Instance | BindingFlags.Public)?.GetValue(value) is Guid id)
                return id;
            return states.GetValue(value, static _ => new State()).Id;
        }

        public static void SetId(IExtensibleObject value, Guid id) => states.GetValue(value, static _ => new State()).Id = id;
    }
}
