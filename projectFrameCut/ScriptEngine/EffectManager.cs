using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using Microsoft.Maui.ApplicationModel;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.DraftStuff;
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
        protected IEffectBundle? ResolveEffectBundle(ClipElementUI clip, Guid bundleId)
        {
            if (clip.EffectBundles is not null && clip.EffectBundles.TryGetValue(bundleId, out var bundle))
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
        protected PSObject NewEffectBundleObject(IEffectBundle bundle)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Id", bundle.Id));
            obj.Properties.Add(new PSNoteProperty("Name", bundle.Name));
            obj.Properties.Add(new PSNoteProperty("TypeName", bundle.TypeName));
            obj.Properties.Add(new PSNoteProperty("FromPlugin", bundle.FromPlugin));
            obj.Properties.Add(new PSNoteProperty("EffectType", bundle.TypeOfEffect.ToString()));
            obj.Properties.Add(new PSNoteProperty("Target", bundle.Target.ToString()));
            obj.Properties.Add(new PSNoteProperty("Enabled", bundle.Enabled));
            obj.Properties.Add(new PSNoteProperty("ParameterCount", bundle.Parameters?.Count ?? 0));
            obj.Properties.Add(new PSNoteProperty("ParameterNames",
                bundle.Parameters?.Keys.ToArray() ?? Array.Empty<string>()));
            obj.Properties.Add(new PSNoteProperty("SettableFieldCount", bundle.SettableFields?.Count ?? 0));
            obj.Properties.Add(new PSNoteProperty("SettableFields",
                bundle.SettableFields?.Values.Select(f => new
                {
                    f.Id,
                    f.DisplayName,
                    f.Description,
                    ValueType = f.ValueType.ToString(),
                    f.DefaultValue,
                    f.MinValue,
                    f.MaxValue,
                    f.PresetOptions,
                    f.Remarks
                }).ToList() ?? new()));
            obj.Properties.Add(new PSNoteProperty("BindedInputId", bundle.BindedInputId));
            obj.Properties.Add(new PSNoteProperty("BindedOutputId", bundle.BindedOutputId));
            obj.Properties.Add(new PSNoteProperty("StartPoint", bundle.StartPoint));
            obj.Properties.Add(new PSNoteProperty("EndPoint", bundle.EndPoint));
            return obj;
        }

        /// <summary>
        /// 构造简化的 EffectBundle PSObject（仅 Id、Name、Type、Enabled 和当前参数值）。
        /// 用于快速查看 Bundle 列表。
        /// </summary>
        protected PSObject NewEffectBundleSummaryObject(IEffectBundle bundle)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Id", bundle.Id));
            obj.Properties.Add(new PSNoteProperty("Name", bundle.Name));
            obj.Properties.Add(new PSNoteProperty("TypeName", bundle.TypeName));
            obj.Properties.Add(new PSNoteProperty("Enabled", bundle.Enabled));
            obj.Properties.Add(new PSNoteProperty("EffectType", bundle.TypeOfEffect.ToString()));
            obj.Properties.Add(new PSNoteProperty("ParameterCount", bundle.Parameters?.Count ?? 0));
            obj.Properties.Add(new PSNoteProperty("ParameterSummary",
                bundle.Parameters is { Count: > 0 }
                    ? string.Join("; ", bundle.Parameters.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"))
                    : "(none)"));
            return obj;
        }

        /// <summary>
        /// 构造 SettableField 元数据 PSObject。
        /// </summary>
        protected PSObject NewSettableFieldObject(EffectBundleSettableFields field)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("Id", field.Id));
            obj.Properties.Add(new PSNoteProperty("DisplayName", field.DisplayName));
            obj.Properties.Add(new PSNoteProperty("Description", field.Description));
            obj.Properties.Add(new PSNoteProperty("ValueType", field.ValueType.ToString()));
            obj.Properties.Add(new PSNoteProperty("DefaultValue", field.DefaultValue));
            obj.Properties.Add(new PSNoteProperty("MinValue", field.MinValue));
            obj.Properties.Add(new PSNoteProperty("MaxValue", field.MaxValue));
            obj.Properties.Add(new PSNoteProperty("PresetOptions", field.PresetOptions));
            obj.Properties.Add(new PSNoteProperty("Remarks", field.Remarks ?? ""));
            return obj;
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
            if (!EnsurePageLoaded(out _)) return;

            var available = EffectServices.GetAvailableEffectBundles();
            if (available.Count == 0)
            {
                WriteWarning("No EffectBundle types available. Plugins may not be initialized yet.");
                return;
            }

            var filtered = available.AsEnumerable();

            if (!string.IsNullOrEmpty(Name))
            {
                var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(Name)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                filtered = filtered.Where(kv =>
                    System.Text.RegularExpressions.Regex.IsMatch(kv.Key, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }

            if (EffectType.HasValue)
                filtered = filtered.Where(kv => kv.Value().TypeOfEffect == EffectType.Value);

            if (Target.HasValue)
                filtered = filtered.Where(kv => kv.Value().Target.HasFlag(Target.Value));

            // 对每种类型实例化一次以获取元数据
            var results = filtered.Select(kv =>
            {
                var instance = kv.Value();
                var obj = new PSObject();
                obj.Properties.Add(new PSNoteProperty("TypeName", kv.Key));
                obj.Properties.Add(new PSNoteProperty("Name", instance.Name));
                obj.Properties.Add(new PSNoteProperty("FromPlugin", instance.FromPlugin));
                obj.Properties.Add(new PSNoteProperty("EffectType", instance.TypeOfEffect.ToString()));
                obj.Properties.Add(new PSNoteProperty("Target", instance.Target.ToString()));
                obj.Properties.Add(new PSNoteProperty("IsMultiInput", instance.IsMultiInput));
                obj.Properties.Add(new PSNoteProperty("SettableFieldCount", instance.SettableFields?.Count ?? 0));
                obj.Properties.Add(new PSNoteProperty("SettableFields",
                    instance.SettableFields?.Values.Select(f => new
                    {
                        f.Id,
                        f.DisplayName,
                        f.Description,
                        ValueType = f.ValueType.ToString(),
                        f.DefaultValue,
                        f.MinValue,
                        f.MaxValue,
                        f.PresetOptions,
                        f.Remarks
                    }).ToList() ?? new()));
                obj.Properties.Add(new PSNoteProperty("ParametersNeeded",
                    instance.ParametersNeeded?.ToArray() ?? Array.Empty<string>()));
                return obj;
            }).ToList();

            WriteObject(results, enumerateCollection: false);
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
            if (clip is null) return;

            if (clip.EffectBundles is null || clip.EffectBundles.Count == 0)
            {
                WriteObject(null); // 返回空
                return;
            }

            var bundles = clip.EffectBundles.Values.AsEnumerable();

            if (BundleId.HasValue)
                bundles = bundles.Where(b => b.Id == BundleId.Value);

            if (!string.IsNullOrEmpty(TypeName))
            {
                var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(TypeName)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                bundles = bundles.Where(b =>
                    System.Text.RegularExpressions.Regex.IsMatch(b.TypeName, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }

            var list = bundles.ToList();

            if (ShowFields)
            {
                // 输出每个 Bundle 的 SettableFields 元数据
                foreach (var bundle in list)
                {
                    if (bundle.SettableFields is null || bundle.SettableFields.Count == 0)
                        continue;

                    WriteObject(NewEffectBundleObject(bundle));
                    foreach (var field in bundle.SettableFields.Values)
                    {
                        WriteObject(NewSettableFieldObject(field));
                    }
                }
            }
            else if (Detailed)
            {
                WriteObject(list.Select(NewEffectBundleObject).ToList(), enumerateCollection: true);
            }
            else
            {
                WriteObject(list.Select(NewEffectBundleSummaryObject).ToList(), enumerateCollection: true);
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

            if (string.IsNullOrEmpty(TypeName))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("TypeName is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            var available = EffectServices.GetAvailableEffectBundles();
            if (!available.TryGetValue(TypeName, out var factory))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"EffectBundle type '{TypeName}' not found. " +
                        "Use Get-ProjectEffectBundleType to see available types."),
                    "EffectBundleTypeNotFound",
                    ErrorCategory.ObjectNotFound,
                    TypeName));
                return;
            }

            if (!ShouldProcess($"Clip '{clip.DisplayName}'", $"Add EffectBundle '{TypeName}'"))
                return;

            try
            {
                var bundle = factory();
                bundle.Name = Name ?? TypeName;
                bundle.Enabled = !Disabled;

                // 通过 SettableFields 设置初始字段值
                if (Fields is { Count: > 0 })
                {
                    if (bundle.SettableFields is null || bundle.SettableFields.Count == 0)
                    {
                        WriteWarning($"EffectBundle '{TypeName}' has no settable fields. Ignoring -Fields parameter.");
                    }
                    else
                    {
                        foreach (var key in Fields.Keys)
                        {
                            var fieldId = key?.ToString();
                            if (string.IsNullOrEmpty(fieldId)) continue;

                            if (!bundle.SettableFields.TryGetValue(fieldId, out var fieldDef))
                            {
                                WriteWarning($"Field '{fieldId}' not found on EffectBundle '{TypeName}'. " +
                                    $"Available fields: {string.Join(", ", bundle.SettableFields.Keys)}");
                                continue;
                            }

                            if (!bundle.HandleSettableFieldsChange(fieldDef, Fields[key]!, out var feedback))
                            {
                                WriteWarning($"Failed to set field '{fieldId}' on EffectBundle '{TypeName}': {feedback}");
                            }
                        }
                    }
                }

                // 添加到 clip
                clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
                clip.EffectBundles[bundle.Id] = bundle;

                if (PassThru)
                    WriteObject(NewEffectBundleObject(bundle));
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(
                    ex,
                    "AddEffectBundleFailed",
                    ErrorCategory.NotSpecified,
                    null));
            }
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

            var bundle = ResolveEffectBundle(clip, BundleId);
            if (bundle is null) return;

            if (!ShouldProcess($"EffectBundle '{bundle.Name}' ({BundleId}) on clip '{clip.DisplayName}'",
                    "Modify effect bundle"))
                return;

            // ── 通用属性 ──
            if (Name is not null)
                bundle.Name = Name;

            if (Enabled.HasValue)
                bundle.Enabled = Enabled.Value;

            // ── 锚点绑定 ──
            if (BindedInputId.HasValue)
                bundle.BindedInputId = BindedInputId.Value;

            if (BindedOutputId.HasValue)
                bundle.BindedOutputId = BindedOutputId.Value;

            // ── 重置为默认 ──
            if (ResetToDefaults)
                bundle.Parameters?.Clear();

            // ── 通过 SettableFields 设置字段值 ──
            if (Fields is { Count: > 0 })
            {
                if (bundle.SettableFields is null || bundle.SettableFields.Count == 0)
                {
                    WriteWarning($"EffectBundle '{bundle.TypeName}' has no settable fields. Ignoring -Fields parameter.");
                }
                else
                {
                    var fieldResultLog = new List<string>();

                    foreach (var key in Fields.Keys)
                    {
                        var fieldId = key?.ToString();
                        if (string.IsNullOrEmpty(fieldId)) continue;

                        if (!bundle.SettableFields.TryGetValue(fieldId, out var fieldDef))
                        {
                            WriteWarning($"Field '{fieldId}' not found on EffectBundle '{bundle.TypeName}'. " +
                                $"Available fields: {string.Join(", ", bundle.SettableFields.Keys)}");
                            continue;
                        }

                        if (bundle.HandleSettableFieldsChange(fieldDef, Fields[key]!, out var feedback))
                        {
                            fieldResultLog.Add($"{fieldId} = {Fields[key]}");
                        }
                        else
                        {
                            WriteWarning($"Failed to set field '{fieldId}' on EffectBundle '{bundle.TypeName}': {feedback}");
                        }
                    }
                }
            }

            if (PassThru)
                WriteObject(NewEffectBundleObject(bundle));
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

            if (clip.EffectBundles is null || !clip.EffectBundles.TryGetValue(BundleId, out var bundle))
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

            clip.EffectBundles.Remove(BundleId);
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
            if (!EnsurePageLoaded(out _)) return;

            if (string.IsNullOrEmpty(TypeName))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("TypeName is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            var available = EffectServices.GetAvailableEffectBundles();
            if (!available.TryGetValue(TypeName, out var factory))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"EffectBundle type '{TypeName}' not found."),
                    "EffectBundleTypeNotFound",
                    ErrorCategory.ObjectNotFound,
                    TypeName));
                return;
            }

            var instance = factory();

            if (instance.SettableFields is null || instance.SettableFields.Count == 0)
            {
                WriteWarning($"EffectBundle '{TypeName}' has no settable fields.");
                return;
            }

            WriteObject(
                instance.SettableFields.Values.Select(NewSettableFieldObject).ToList(),
                enumerateCollection: true);
        }
    }
}
