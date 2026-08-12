using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using Microsoft.Maui.ApplicationModel;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;

namespace projectFrameCut.ScriptEngine
{
    // ────────────────────────────────────────────────────────────────
    //  EffectBundleCmdletBase — EffectBundle CRUD 共享基类
    //  复用 DraftPageCmdletBase 的 UI 线程调度与辅助方法
    // ────────────────────────────────────────────────────────────────
    public abstract class EffectBundleCmdletBase : PSCmdlet
    {
        /// <summary>写操作设为 true 以自动调度到 UI 线程。</summary>
        protected virtual bool RequiresUIThread => false;

        /// <summary>子类在此实现核心逻辑。</summary>
        protected abstract void ProcessRecordImpl();

        protected override void ProcessRecord()
        {
            if (RequiresUIThread && !MainThread.IsMainThread)
            {
                Exception? captured = null;
                using var ev = new ManualResetEventSlim(false);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    try
                    {
                        ProcessRecordImpl();
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                    finally
                    {
                        ev.Set();
                    }
                });

                ev.Wait();

                if (captured != null)
                {
                    ThrowTerminatingError(new ErrorRecord(
                        captured,
                        "UIThreadCmdletError",
                        ErrorCategory.InvalidOperation,
                        null));
                }
            }
            else
            {
                ProcessRecordImpl();
            }
        }

        // ─── 辅助方法 ───────────────────────────────────────────

        protected DraftPage? GetCurrentPage()
        {
            return SessionState.PSVariable.GetValue("page") as DraftPage;
        }

        protected bool EnsurePageLoaded(out DraftPage? page)
        {
            page = GetCurrentPage();
            if (page is null)
            {
                WriteError(new ErrorRecord(
                    new InvalidOperationException("No DraftPage is loaded. Open a project first."),
                    "DraftPageNotLoaded",
                    ErrorCategory.InvalidOperation,
                    null));
                return false;
            }
            return true;
        }

        protected ClipElementUI? ResolveClip(DraftPage page, Guid id)
        {
            if (page.Clips.TryGetValue(id, out var clip))
                return clip;

            WriteError(new ErrorRecord(
                new ArgumentException($"Clip with Id '{id}' not found."),
                "ClipNotFound",
                ErrorCategory.ObjectNotFound,
                id));
            return null;
        }

        /// <summary>
        /// 按 Guid 查找 clip 上的 EffectBundle，未找到时写非终止错误。
        /// </summary>
        protected IEffectProvider? ResolveEffectBundle(ClipElementUI clip, Guid bundleId)
        {
            if (clip.EffectProviders is not null && clip.EffectProviders.TryGetValue(bundleId, out var bundle))
                return bundle;

            WriteError(new ErrorRecord(
                new ArgumentException($"EffectBundle with Id '{bundleId}' not found on clip '{clip.DisplayName}'."),
                "EffectBundleNotFound",
                ErrorCategory.ObjectNotFound,
                bundleId));
            return null;
        }

        // ─── PSObject 输出构建器 ───────────────────────────────

        /// <summary>构造标准的 EffectBundle PSObject（含元数据概要）。</summary>
        protected PSObject NewEffectBundleObject(IEffectProvider bundle)
        {
            var obj = NewEffectBundleSummaryObject(bundle);
            obj.Properties.Add(new PSNoteProperty("FromPlugin", bundle.FromPlugin));
            obj.Properties.Add(new PSNoteProperty("Target", bundle.Target.ToString()));
            obj.Properties.Add(new PSNoteProperty("InputSource", bundle.HasMainPictureInput() ? bundle.GetMainInputSource() : null));
            obj.Properties.Add(new PSNoteProperty("IsFinalOutput", bundle.IsFinalOutputSource()));
            obj.Properties.Add(new PSNoteProperty("AnchorsBindingState", ToHashtable(bundle.AnchorsBindingState)));
            obj.Properties.Add(new PSNoteProperty("Fields", bundle.Fields.Values.Select(NewSettableFieldObject).ToArray()));
            obj.Properties.Add(new PSNoteProperty("MetaData", ToHashtable(bundle.MetaData)));
            return obj;
        }

        /// <summary>
        /// 构造简化的 EffectBundle PSObject（仅 Id、Name、Type、Enabled 和当前参数值）。
        /// 用于快速查看 Bundle 列表。
        /// </summary>
        protected PSObject NewEffectBundleSummaryObject(IEffectProvider bundle)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Id", bundle.Id));
            obj.Properties.Add(new PSNoteProperty("Name", bundle.Name));
            obj.Properties.Add(new PSNoteProperty("TypeName", bundle.TypeName));
            obj.Properties.Add(new PSNoteProperty("Enabled", bundle.Enabled));
            obj.Properties.Add(new PSNoteProperty("EffectType", bundle.TypeOfEffect.ToString()));
            obj.Properties.Add(new PSNoteProperty("ParameterCount", bundle.Fields?.Count ?? 0));
            obj.Properties.Add(new PSNoteProperty("ParameterSummary",
                bundle.Fields is { Count: > 0 }
                    ? string.Join("; ", bundle.Fields.Select(kv => $"{kv.Key}={GetFieldValue(kv.Value) ?? "null"}"))
                    : "(none)"));
            return obj;
        }

        /// <summary>
        /// 构造 SettableField 元数据 PSObject。
        /// </summary>
        protected PSObject NewSettableFieldObject(IEffectArgumentField field)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Id", field.Id));
            obj.Properties.Add(new PSNoteProperty("FieldType", field.FieldType.ToString()));
            obj.Properties.Add(new PSNoteProperty("Value", GetFieldValue(field)));
            obj.Properties.Add(new PSNoteProperty("IsDynamic", field.IsDynamic));
            obj.Properties.Add(new PSNoteProperty("DefaultValue", field.DefaultValue));
            obj.Properties.Add(new PSNoteProperty("MinValue", field.MinValue));
            obj.Properties.Add(new PSNoteProperty("MaxValue", field.MaxValue));
            obj.Properties.Add(new PSNoteProperty("PresetOptions", field.PresetOptions));
            obj.Properties.Add(new PSNoteProperty("Remarks", field.Remarks ?? ""));
            return obj;
        }

        protected int ApplyFields(IEffectProvider provider, Hashtable values)
        {
            var fields = provider.Fields;
            int changed = 0;
            foreach (DictionaryEntry entry in values)
            {
                var fieldId = entry.Key?.ToString() ?? string.Empty;
                if (!fields.TryGetValue(fieldId, out var field))
                {
                    WriteError(new ErrorRecord(
                        new ArgumentException($"Unknown field '{fieldId}' for effect provider '{provider.TypeName}'."),
                        "EffectProviderFieldNotFound", ErrorCategory.InvalidArgument, fieldId));
                    continue;
                }

                try
                {
                    fields[fieldId] = new StaticEffectArgumentField
                    {
                        Id = fieldId,
                        FieldType = field.FieldType,
                        Value = ConvertFieldValue(field, entry.Value),
                        DefaultValue = field.DefaultValue,
                        MinValue = field.MinValue,
                        MaxValue = field.MaxValue,
                        PresetOptions = field.PresetOptions,
                        Remarks = field.Remarks,
                    };
                    provider.ClearFieldBinding(fieldId);
                    changed++;
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
                {
                    WriteError(new ErrorRecord(ex, "InvalidEffectProviderFieldValue", ErrorCategory.InvalidArgument, entry.Value));
                }
            }
            provider.Fields = fields;
            return changed;
        }

        private static object? GetFieldValue(IEffectArgumentField field) => field switch
        {
            StaticEffectArgumentField staticField => staticField.Value,
            DynamicEffectParamField dynamicField => dynamicField.StaticFallbackValue,
            _ => field.GetGetter()(),
        };

        private static Hashtable ToHashtable<TValue>(IEnumerable<KeyValuePair<string, TValue>> values)
        {
            var result = new Hashtable(StringComparer.Ordinal);
            foreach (var (key, value) in values) result[key] = value;
            return result;
        }

        private static object ConvertFieldValue(IEffectArgumentField field, object? rawValue)
        {
            rawValue = EffectParamConvert.Normalize(rawValue);
            var baseType = field.FieldType & (EffectArgumentFieldType)0xFFFF;
            object converted = baseType switch
            {
                EffectArgumentFieldType.Integer when EffectParamConvert.TryConvertToInt(rawValue, out var intValue) => intValue,
                EffectArgumentFieldType.UnsignedInteger when EffectParamConvert.TryConvertToUShort(rawValue, out var unsignedValue) => unsignedValue,
                EffectArgumentFieldType.Numeric when EffectParamConvert.TryConvertToFloat(rawValue, out var numericValue) => numericValue,
                EffectArgumentFieldType.Boolean when EffectParamConvert.TryConvertToBool(rawValue, out var boolValue) => boolValue,
                EffectArgumentFieldType.Long => Convert.ToInt64(rawValue, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.UnsignedLong => Convert.ToUInt64(rawValue, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.String => rawValue?.ToString() ?? string.Empty,
                _ when rawValue is not null => rawValue,
                _ => throw new ArgumentException("The value cannot be null."),
            };
            if (field.PresetOptions is { Length: > 0 }
                && converted is string text
                && !field.PresetOptions.Contains(text, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Value must be one of: {string.Join(", ", field.PresetOptions)}.");
            return converted;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  #region Available EffectBundle Types
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 列出工程中所有可用的 EffectBundle 类型及其元数据。
    /// </summary>
    [Cmdlet(VerbsCommon.Get, "EffectBundleTypes")]
    public sealed class GetProjectEffectBundleTypeCommand : EffectBundleCmdletBase
    {
        [Parameter]
        public string? Name { get; set; }

        [Parameter]
        public EffectType? EffectType { get; set; }

        [Parameter]
        public EffectTarget? Target { get; set; }

        protected override void ProcessRecordImpl()
        {
            foreach (var (typeName, factory) in EffectServices.GetAvailableEffectProviders())
            {
                var provider = factory();
                if (!string.IsNullOrWhiteSpace(Name)
                    && !typeName.Contains(Name, StringComparison.OrdinalIgnoreCase)
                    && !provider.Name.Contains(Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (EffectType.HasValue && provider.TypeOfEffect != EffectType.Value) continue;
                if (Target.HasValue && !provider.Target.HasFlag(Target.Value)) continue;

                var obj = NewEffectBundleObject(provider);
                obj.Properties.Add(new PSNoteProperty("RegistrationKey", typeName));
                WriteObject(obj);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  #region EffectBundle CRUD on Clip
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取 clip 上的 EffectBundle 列表。
    /// </summary>
    [Cmdlet(VerbsCommon.Get, "ProjectClipEffectBundle")]
    public sealed class GetProjectClipEffectBundleCommand : EffectBundleCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public Guid ClipId { get; set; }

        [Parameter]
        public Guid? BundleId { get; set; }

        [Parameter]
        public string? TypeName { get; set; }

        [Parameter]
        public SwitchParameter ShowFields { get; set; }

        /// <summary>
        /// 如果指定，则输出完整详情而非摘要。
        /// </summary>
        [Parameter]
        public SwitchParameter Detailed { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var clip = ResolveClip(page!, ClipId);
            if (clip?.EffectProviders is null) return;

            var providers = clip.EffectProviders.Values.AsEnumerable();
            if (BundleId.HasValue) providers = providers.Where(provider => provider.Id == BundleId.Value);
            if (!string.IsNullOrWhiteSpace(TypeName))
                providers = providers.Where(provider => provider.TypeName.Equals(TypeName, StringComparison.OrdinalIgnoreCase));

            foreach (var provider in providers)
            {
                var obj = Detailed ? NewEffectBundleObject(provider) : NewEffectBundleSummaryObject(provider);
                if (ShowFields && !Detailed)
                    obj.Properties.Add(new PSNoteProperty("Fields", provider.Fields.Values.Select(NewSettableFieldObject).ToArray()));
                WriteObject(obj);
            }
        }
    }

    /// <summary>
    /// 为 clip 添加一个 EffectBundle。
    /// </summary>
    [Cmdlet(VerbsCommon.Add, "ProjectClipEffectBundle", SupportsShouldProcess = true)]
    public sealed class AddProjectClipEffectBundleCommand : EffectBundleCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public Guid ClipId { get; set; }

        /// <summary>
        /// EffectBundle 的类型名称，对应 EffectServices.GetAvailableEffectBundles() 的 key，
        /// 例如 "Blur"、"Crop"、"Flip" 等。
        /// </summary>
        [Parameter(Mandatory = true, Position = 1)]
        public string? TypeName { get; set; }

        /// <summary>
        /// 此 Bundle 实例的自定义名称。不指定则使用 TypeName。
        /// </summary>
        [Parameter]
        public string? Name { get; set; }

        /// <summary>
        /// 通过 SettableFields 机制设置的初始字段值。
        /// Key 为字段 Id，Value 为目标值。
        /// </summary>
        [Parameter]
        public Hashtable? Fields { get; set; }

        /// <summary>
        /// 是否默认启用。默认为 true。
        /// </summary>
        [Parameter]
        public SwitchParameter Disabled { get; set; }

        [Parameter]
        public SwitchParameter PassThru { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var clip = ResolveClip(page!, ClipId);
            if (clip is null) return;
            if (string.IsNullOrWhiteSpace(TypeName)
                || !EffectServices.GetAvailableEffectProviders().TryGetValue(TypeName, out var factory))
            {
                WriteError(new ErrorRecord(new ArgumentException($"Effect provider type '{TypeName}' was not found."),
                    "EffectProviderTypeNotFound", ErrorCategory.ObjectNotFound, TypeName));
                return;
            }

            var provider = factory();
            if (!EffectBindingHelper.AreTargetsCompatible(provider.Target, clip.GetEffectTarget()))
            {
                WriteError(new ErrorRecord(new ArgumentException($"Effect provider '{TypeName}' is not compatible with clip '{clip.DisplayName}'."),
                    "EffectProviderTargetMismatch", ErrorCategory.InvalidArgument, TypeName));
                return;
            }
            if (!string.IsNullOrWhiteSpace(Name)) provider.Name = Name;
            provider.Enabled = !Disabled;
            if (Fields is not null) ApplyFields(provider, Fields);

            var action = $"Add effect provider '{provider.Name}' to clip '{clip.DisplayName}'";
            if (!ShouldProcess(clip.DisplayName, action)) return;
            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
            clip.EffectProviders[provider.Id] = provider;
            EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, provider, clip.GetEffectTarget());
            ClipInfoBuilder.RebuildAllEffects(clip);
            page!.RefreshPropertyPanel(clip);
            if (PassThru) WriteObject(NewEffectBundleObject(provider));
        }
    }

    /// <summary>
    /// 修改 clip 上的 EffectBundle 属性。
    /// 核心功能是通过 SettableFields 机制设置字段值，同时支持修改 Name/Enabled 等通用属性。
    /// </summary>
    [Cmdlet(VerbsCommon.Set, "ProjectClipEffectBundle", SupportsShouldProcess = true)]
    public sealed class SetProjectClipEffectBundleCommand : EffectBundleCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public Guid ClipId { get; set; }

        [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
        public Guid BundleId { get; set; }

        /// <summary>
        /// 新的名称。
        /// </summary>
        [Parameter]
        public string? Name { get; set; }

        /// <summary>
        /// 启用或禁用此效果。
        /// </summary>
        [Parameter]
        public bool? Enabled { get; set; }

        /// <summary>
        /// 通过 SettableFields 机制设置的字段值。
        /// Key 为 SettableFields 的 Id，Value 为目标值。
        /// </summary>
        [Parameter]
        public Hashtable? Fields { get; set; }

        /// <summary>
        /// 清空所有参数并重置为默认值后，再应用 Fields（若有）。
        /// 注意：EffectBundle 接口本身不提供重置方法，此开关将清空 Parameters 字典。
        /// 各个 EffectBundle 会通过 Create() 重新生成工厂，但 Parameters 需要手动重建。
        /// 此参数仅清空 Parameters 字典，不会重设 SettableFields 的定义。
        /// </summary>
        [Parameter]
        public SwitchParameter ResetToDefaults { get; set; }

        /// <summary>
        /// 设置 BindedInputId（输入锚点绑定）。
        /// </summary>
        [Parameter]
        public Guid? BindedInputId { get; set; }

        /// <summary>
        /// 设置 BindedOutputId（输出锚点绑定）。
        /// </summary>
        [Parameter]
        public Guid? BindedOutputId { get; set; }

        [Parameter]
        public SwitchParameter PassThru { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var clip = ResolveClip(page!, ClipId);
            if (clip is null) return;
            var provider = ResolveEffectBundle(clip, BundleId);
            if (provider is null) return;

            var action = $"Update effect provider '{provider.Name}' on clip '{clip.DisplayName}'";
            if (!ShouldProcess(clip.DisplayName, action)) return;

            if (ResetToDefaults)
            {
                if (!EffectServices.GetAvailableEffectProviders().TryGetValue(provider.TypeName, out var factory))
                {
                    WriteError(new ErrorRecord(new InvalidOperationException($"Factory for effect provider '{provider.TypeName}' is unavailable."),
                        "EffectProviderFactoryNotFound", ErrorCategory.ObjectNotFound, provider.TypeName));
                    return;
                }
                provider.Fields = factory().Fields;
                foreach (var fieldId in provider.EnumerateFieldBindings().Select(binding => binding.Key).ToArray())
                    provider.ClearFieldBinding(fieldId);
            }
            if (!string.IsNullOrWhiteSpace(Name)) provider.Name = Name;
            if (Enabled.HasValue) provider.Enabled = Enabled.Value;
            if (Fields is not null) ApplyFields(provider, Fields);

            if (BindedInputId.HasValue)
            {
                if (!provider.HasMainPictureInput())
                {
                    WriteError(new ErrorRecord(new InvalidOperationException($"Effect provider '{provider.TypeName}' has no single main picture input."),
                        "EffectProviderHasNoMainInput", ErrorCategory.InvalidOperation, provider));
                }
                else
                {
                    provider.SetMainInputSource(BindedInputId.Value);
                }
            }
            if (BindedOutputId.HasValue)
            {
                if (BindedOutputId.Value == IEffectProvider.OutputAnchorGUID)
                    EffectBindingHelper.SetFinalOutput(clip.EffectProviders!, provider.Id);
                else if (BindedOutputId.Value == IEffectProvider.NoConnectionGUID)
                    provider.SetFinalOutputSource(false);
                else
                    WriteError(new ErrorRecord(new ArgumentException("BindedOutputId must be OutputAnchorGUID or NoConnectionGUID."),
                        "InvalidEffectProviderOutputBinding", ErrorCategory.InvalidArgument, BindedOutputId));
            }

            ClipInfoBuilder.RebuildAllEffects(clip);
            page!.RefreshPropertyPanel(clip);
            if (PassThru) WriteObject(NewEffectBundleObject(provider));
        }
    }

    /// <summary>
    /// 从 clip 移除一个 EffectBundle。
    /// </summary>
    [Cmdlet(VerbsCommon.Remove, "ProjectClipEffectBundle", SupportsShouldProcess = true)]
    public sealed class RemoveProjectClipEffectBundleCommand : EffectBundleCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public Guid ClipId { get; set; }

        [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
        public Guid BundleId { get; set; }

        [Parameter]
        public SwitchParameter Force { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var clip = ResolveClip(page!, ClipId);
            if (clip is null) return;

            if (clip.EffectProviders is null || !clip.EffectProviders.TryGetValue(BundleId, out var bundle))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"EffectBundle with Id '{BundleId}' not found on clip '{clip.DisplayName}'."),
                    "EffectBundleNotFound",
                    ErrorCategory.ObjectNotFound,
                    BundleId));
                return;
            }

            var action = $"Remove EffectBundle '{bundle.Name}' ({BundleId}) from clip '{clip.DisplayName}'";
            if (!Force && !ShouldProcess(clip.DisplayName, bundle.Name, action))
                return;

            EffectBindingHelper.RemoveProvider(clip.EffectProviders, BundleId);
            ClipInfoBuilder.RebuildAllEffects(clip);
            page!.RefreshPropertyPanel(clip);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  #region Utility: Inspect / Debug
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取指定 EffectBundle 类型的所有 SettableFields 定义。
    /// 用于脚本编写时查看可设置的字段。
    /// </summary>
    [Cmdlet("Get", "EffectBundleField")]
    public sealed class GetEffectBundleFieldCommand : EffectBundleCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string? TypeName { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (string.IsNullOrWhiteSpace(TypeName)
                || !EffectServices.GetAvailableEffectProviders().TryGetValue(TypeName, out var factory))
            {
                WriteError(new ErrorRecord(new ArgumentException($"Effect provider type '{TypeName}' was not found."),
                    "EffectProviderTypeNotFound", ErrorCategory.ObjectNotFound, TypeName));
                return;
            }
            foreach (var field in factory().Fields.Values)
                WriteObject(NewSettableFieldObject(field));
        }
    }
}
