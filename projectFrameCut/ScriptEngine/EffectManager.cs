using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;
using Microsoft.Maui.ApplicationModel;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.DraftStuff;
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
            // SettableFields / BindedInputId / BindedOutputId / StartPoint / EndPoint were removed from the provider API.
            throw new NotImplementedException("NewEffectBundleObject was disabled after the IEffectBundle removal.");
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
            // IsMultiInput / SettableFields / ParametersNeeded were removed from the provider API.
            throw new NotImplementedException("Get-ProjectEffectBundleType was disabled after the IEffectBundle removal.");
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
            // SettableFields / NewEffectBundleObject were removed from the provider API.
            throw new NotImplementedException("Get-ProjectClipEffectBundle was disabled after the IEffectBundle removal.");
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
            // SettableFields / HandleSettableFieldsChange / NewEffectBundleObject were removed from the provider API.
            throw new NotImplementedException("Add-ProjectClipEffectBundle was disabled after the IEffectBundle removal.");
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
            // BindedInputId / BindedOutputId / SettableFields / HandleSettableFieldsChange were removed from the provider API.
            throw new NotImplementedException("Set-ProjectClipEffectBundle was disabled after the IEffectBundle removal.");
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

            clip.EffectProviders.Remove(BundleId);
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
            // SettableFields were removed from the provider API.
            throw new NotImplementedException("Get-EffectBundleField was disabled after the IEffectBundle removal.");
        }
    }
}
