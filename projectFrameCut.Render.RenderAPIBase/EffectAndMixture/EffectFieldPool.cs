using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture
{
    /// <summary>
    /// Global static pool of <see cref="IEffectArgumentField"/> instances keyed by field Id.
    /// Static fields with the same Id and value semantics are shared (de-duplicated) to reduce resource overhead.
    /// Dynamic fields (bound to value providers) are stored per binding source.
    /// </summary>
    public static class EffectFieldPool
    {
        /// <summary>
        /// Global store: key = field Id, value = actual field instance.
        /// </summary>
        private static readonly ConcurrentDictionary<string, IEffectArgumentField> _store = new();

        /// <summary>
        /// Free-field registry keyed by globally unique Guid.
        /// Written atomically via <see cref="Interlocked.Exchange(ref object, object)"/> on import/clear.
        /// </summary>
        private static volatile ConcurrentDictionary<Guid, FreeFieldEntry> _freeFields = new();

        // ── legacy _store APIs (unchanged) ────────────────────────────────

        /// <summary>
        /// Get an existing field from the pool, or create and store a new one via the factory.
        /// For static fields, the factory is only called on first access; subsequent calls with the same Id
        /// return the same (shared) instance.
        /// </summary>
        public static IEffectArgumentField GetOrAdd(string id, Func<IEffectArgumentField> factory)
        {
            return _store.GetOrAdd(id, _ => factory());
        }

        /// <summary>
        /// Bind a provider field to a dynamic source.
        /// </summary>
        public static void SetBound(IEffectProvider provider, string fieldId, string? boundSourceId, object? staticFallback)
        {
            var field = new DynamicEffectParamField
            {
                Id = fieldId,
                FieldType = EffectArgumentFieldType.Unknown, // will be updated by caller
                BoundProviderId = boundSourceId,
                StaticFallbackValue = staticFallback,
            };
            _store[fieldId] = field;
        }

        /// <summary>
        /// Unbind a provider field (revert to static).
        /// </summary>
        public static void Unbind(IEffectProvider provider, string fieldId)
        {
            if (_store.TryGetValue(fieldId, out var field) && field is DynamicEffectParamField)
            {
                _store.TryRemove(fieldId, out _);
            }
        }

        // ── free-field APIs ────────────────────────────────────────────────

        /// <summary>
        /// Register a free field with an explicit global id.
        /// </summary>
        public static FreeFieldEntry RegisterFreeField(Guid globalId, IEffectArgumentField field, string? ownerHint = null)
        {
            var entry = new FreeFieldEntry { GlobalId = globalId, Field = field, OwnerHint = ownerHint };
            _freeFields[globalId] = entry;
            return entry;
        }

        /// <summary>
        /// Register a free field with an auto-generated global id.
        /// </summary>
        public static FreeFieldEntry RegisterFreeField(IEffectArgumentField field, string? ownerHint = null)
        {
            return RegisterFreeField(Guid.NewGuid(), field, ownerHint);
        }

        /// <summary>
        /// Returns a snapshot of all currently registered free fields.
        /// </summary>
        public static IReadOnlyList<FreeFieldEntry> EnumerateFreeFields()
        {
            var snapshot = _freeFields;
            var list = new List<FreeFieldEntry>(snapshot.Count);
            foreach (var kv in snapshot)
                list.Add(kv.Value);
            return list;
        }

        /// <summary>
        /// Try to look up a free field by its global id.
        /// </summary>
        public static bool TryGetFreeField(Guid globalId, out FreeFieldEntry entry)
        {
            return _freeFields.TryGetValue(globalId, out entry!);
        }

        /// <summary>
        /// Remove a free field from the registry.
        /// </summary>
        public static bool RemoveFreeField(Guid globalId)
        {
            return _freeFields.TryRemove(globalId, out _);
        }

        /// <summary>
        /// Atomically clear the free-field registry. Used when loading a full draft.
        /// </summary>
        public static void ClearFreeFields()
        {
            Interlocked.Exchange(ref _freeFields, new ConcurrentDictionary<Guid, FreeFieldEntry>());
        }

        /// <summary>
        /// Rebuild an <see cref="IEffectArgumentField"/> from its serialized DTO.
        /// </summary>
        public static IEffectArgumentField? RebuildField(FreeEffectFieldJSONStructure dto)
        {
            if (dto == null) return null;

            if (!Enum.TryParse<EffectArgumentFieldType>(dto.FieldType, ignoreCase: true, out var fieldType))
                fieldType = EffectArgumentFieldType.Unknown;

            var isDynamic = dto.IsBound || string.Equals(dto.TypeName, "DynamicEffectParamField", StringComparison.Ordinal);

            if (isDynamic)
            {
                var field = new DynamicEffectParamField
                {
                    Id = dto.Id,
                    FieldType = fieldType,
                    BoundProviderId = dto.BoundSourceId,
                    StaticFallbackValue = EffectParamConvert.Normalize(dto.StaticValue),
                    DefaultValue = dto.DefaultValue,
                    MinValue = dto.MinValue,
                    MaxValue = dto.MaxValue,
                    PresetOptions = dto.PresetOptions,
                    Remarks = dto.Remarks,
                };
                return field;
            }

            var staticField = new StaticEffectArgumentField
            {
                Id = dto.Id,
                FieldType = fieldType,
                Value = EffectParamConvert.Normalize(dto.StaticValue) ?? new object(),
                DefaultValue = dto.DefaultValue,
                MinValue = dto.MinValue,
                MaxValue = dto.MaxValue,
                PresetOptions = dto.PresetOptions,
                Remarks = dto.Remarks,
            };
            return staticField;
        }

        /// <summary>
        /// Export all free fields as serializable DTOs.
        /// </summary>
        public static FreeEffectFieldJSONStructure[] ExportFreeFields()
        {
            var snapshot = _freeFields;
            var result = new List<FreeEffectFieldJSONStructure>(snapshot.Count);
            foreach (var kv in snapshot)
            {
                var entry = kv.Value;
                var f = entry.Field;
                var dto = new FreeEffectFieldJSONStructure
                {
                    GlobalId = entry.GlobalId,
                    OwnerHint = entry.OwnerHint,
                    Id = f.Id,
                    TypeName = f.TypeName,
                    FieldType = f.FieldType.ToString(),
                    DefaultValue = f.DefaultValue,
                    MinValue = f.MinValue,
                    MaxValue = f.MaxValue,
                    PresetOptions = f.PresetOptions,
                    Remarks = f.Remarks,
                    IsBound = f.IsDynamic,
                    BoundSourceId = f is DynamicEffectParamField df ? df.BoundProviderId : null,
                    StaticValue = f.GetGetter()?.Invoke(),
                };
                result.Add(dto);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Atomically replace the free-field registry from deserialized DTOs.
        /// Used on full-draft load and slot-switch to prevent cross-project leakage.
        /// </summary>
        public static void ImportFreeFields(FreeEffectFieldJSONStructure[]? entries)
        {
            var next = new ConcurrentDictionary<Guid, FreeFieldEntry>();
            if (entries != null)
            {
                foreach (var dto in entries)
                {
                    if (dto.GlobalId == Guid.Empty) continue;
                    var field = RebuildField(dto);
                    if (field is null) continue;
                    next[dto.GlobalId] = new FreeFieldEntry
                    {
                        GlobalId = dto.GlobalId,
                        Field = field,
                        OwnerHint = dto.OwnerHint,
                    };
                }
            }
            Interlocked.Exchange(ref _freeFields, next);
        }

        /// <summary>
        /// One-time migration helper: adopt a field from the legacy <c>_store</c> into the free-field registry.
        /// Currently a no-op in practice because <c>_store</c> only contains bound dynamic fields.
        /// </summary>
        public static FreeFieldEntry? AdoptFromStore(string fieldId, Guid? globalId = null)
        {
            if (!_store.TryGetValue(fieldId, out var field)) return null;
            var id = globalId ?? Guid.NewGuid();
            var entry = new FreeFieldEntry { GlobalId = id, Field = field };
            _freeFields[id] = entry;
            return entry;
        }
    }

    /// <summary>
    /// Wraps an <see cref="IEffectArgumentField"/> with a globally unique identity
    /// for persistence outside of any <see cref="IEffectProvider"/> (a "free field").
    /// </summary>
    public sealed record FreeFieldEntry
    {
        public required Guid GlobalId { get; init; }
        public required IEffectArgumentField Field { get; init; }
        public string? OwnerHint { get; init; }
    }
}