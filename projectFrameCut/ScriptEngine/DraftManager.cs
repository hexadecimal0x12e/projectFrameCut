using Microsoft.Maui.ApplicationModel;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Threading;

namespace projectFrameCut.ScriptEngine
{
    // ────────────────────────────────────────────────────────────────
    //  DraftPageCmdletBase — 共享抽象基类
    //  提供自动 UI 线程调度 & 通用辅助方法
    // ────────────────────────────────────────────────────────────────
    public abstract class DraftPageCmdletBase : PSCmdlet
    {
        /// <summary>
        /// 写操作（修改时间线）设为 true 以自动调度到 UI 线程。
        /// 只读操作保持 false 直接在管道线程执行。
        /// </summary>
        protected virtual bool RequiresUIThread => false;

        /// <summary>
        /// 子类在此方法中实现核心逻辑，而非在 ProcessRecord 中。
        /// 基类自动处理 UI 线程调度。
        /// </summary>
        protected abstract void ProcessRecordImpl();

        protected override void ProcessRecord()
        {
            // ProcessRecordImpl 必须保留在 PowerShell 管道线程中运行，
            // 否则 WriteObject/WriteError/ShouldProcess 会违反 PSCmdlet 线程约束。
            ProcessRecordImpl();
        }

        /// <summary>
        /// 仅将 UI/模型操作调度到 UI 线程；调用方仍在管道线程中，
        /// 可以安全地调用 WriteObject、WriteError 和 ShouldProcess。
        /// </summary>
        protected T InvokeOnUIThread<T>(Func<T> action)
        {
            if (!RequiresUIThread || MainThread.IsMainThread)
                return action();

            T result = default!;
            Exception? captured = null;
            using var ev = new ManualResetEventSlim(false);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    result = action();
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

            if (captured is not null)
                throw captured;

            return result;
        }

        protected void InvokeOnUIThread(Action action)
        {
            InvokeOnUIThread(() =>
            {
                action();
                return true;
            });
        }

        // ─── 辅助方法 ───────────────────────────────────────────

        /// <summary>从运行空间读取 $page 变量。</summary>
        protected DraftPage? GetCurrentPage()
        {
            return SessionState.PSVariable.GetValue("page") as DraftPage;
        }

        /// <summary>
        /// 确保 DraftPage 已加载，否则写非终止错误。
        /// 返回 false 表示调用者应提前 return。
        /// </summary>
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

        /// <summary>按 Guid 查找 Clip，未找到时写非终止错误。</summary>
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

        /// <summary>从 clip 的效果字典中按名称查找效果。</summary>
        protected IEffect? ResolveEffect(ClipElementUI clip, string name)
        {
            if (clip.Effects is null || !clip.Effects.TryGetValue(name, out var effect))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Effect '{name}' not found on clip '{clip.DisplayName}'."),
                    "EffectNotFound",
                    ErrorCategory.ObjectNotFound,
                    name));
                return null;
            }
            return effect;
        }

        // ─── PSObject 输出构建器 ───────────────────────────────

        /// <summary>构造标准的 Clip PSObject。</summary>
        protected PSObject NewClipObject(ClipElementUI c)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty(nameof(c.Id), c.Id));
            obj.Properties.Add(new PSNoteProperty("Name", c.DisplayName));
            obj.Properties.Add(new PSNoteProperty("Type", c.ClipType.ToString()));
            obj.Properties.Add(new PSNoteProperty("Track", c.origTrack));
            obj.Properties.Add(new PSNoteProperty("StartX", Math.Round(c.origX, 1)));
            obj.Properties.Add(new PSNoteProperty("Length", Math.Round(c.origLength, 1)));
            obj.Properties.Add(new PSNoteProperty("Source", c.SourcePath ?? ""));
            obj.Properties.Add(new PSNoteProperty("Width", c.TargetWidth));
            obj.Properties.Add(new PSNoteProperty("Height", c.TargetHeight));
            obj.Properties.Add(new PSNoteProperty("EffectCount", c.Effects?.Count ?? 0));
            obj.Properties.Add(new PSNoteProperty("VisibleOnUI", c.ShouldDisplayInUI));
            if (SettingsManager.IsBoolSettingTrueOrDefault("Security_Script_AllowAccessPageObject", false)) obj.Properties.Add(new PSNoteProperty("InnerClip", c));
            return obj;
        }

        /// <summary>构造标准的 Asset PSObject。</summary>
        protected PSObject NewAssetObject(AssetItem a)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty(nameof(a.AssetId), a.AssetId));
            obj.Properties.Add(new PSNoteProperty(nameof(a.Name), a.Name));
            obj.Properties.Add(new PSNoteProperty("Type", a.AssetType.ToString()));
            obj.Properties.Add(new PSNoteProperty(nameof(a.Path), a.Path));
            obj.Properties.Add(new PSNoteProperty(nameof(a.Duration), a.Duration));
            obj.Properties.Add(new PSNoteProperty(nameof(a.Width), a.Width));
            obj.Properties.Add(new PSNoteProperty(nameof(a.Height), a.Height));
            return obj;
        }

        /// <summary>构造标准的 Effect PSObject。</summary>
        protected PSObject NewEffectObject(string name, IEffect effect)
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty(nameof(name), name));
            obj.Properties.Add(new PSNoteProperty("EffectType", effect.TypeOfEffect.ToString()));
            obj.Properties.Add(new PSNoteProperty(nameof(effect.Enabled), effect.Enabled));
            obj.Properties.Add(new PSNoteProperty(nameof(effect.Index), effect.Index));
            obj.Properties.Add(new PSNoteProperty("ParameterCount", effect.Parameters?.Count ?? 0));
            obj.Properties.Add(new PSNoteProperty("ParameterNames",
                effect.Parameters?.Keys.ToArray() ?? Array.Empty<string>()));
            return obj;
        }

        /// <summary>构造标准的 Track PSObject。</summary>
        protected PSObject NewTrackObject(DraftPage page, int trackId)
        {
            var clipsOnTrack = page.Clips.Values
                .Where(c => c.origTrack == trackId)
                .ToList();

            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("TrackId", trackId));
            obj.Properties.Add(new PSNoteProperty("ClipCount", clipsOnTrack.Count));
            obj.Properties.Add(new PSNoteProperty("ClipIds",
                clipsOnTrack.Select(c => c.Id).ToArray()));
            return obj;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  #region Clip CRUD
    // ════════════════════════════════════════════════════════════════

    [Cmdlet(VerbsCommon.Get, "ProjectClip")]
    public sealed class GetProjectClipCommand : DraftPageCmdletBase
    {
        [Parameter(Position = 0)]
        public Guid? Id { get; set; }

        [Parameter]
        public string? Name { get; set; }

        [Parameter]
        public int? Track { get; set; }

        [Parameter]
        public ClipMode? Type { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            var clips = page!.Clips.Values.AsEnumerable();

            if (Id.HasValue)
                clips = clips.Where(c => c.Id == Id.Value);

            if (!string.IsNullOrEmpty(Name))
            {
                var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(Name)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                clips = clips.Where(c =>
                    System.Text.RegularExpressions.Regex.IsMatch(c.DisplayName ?? "", pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }

            if (Track.HasValue)
                clips = clips.Where(c => c.origTrack == Track.Value);

            if (Type.HasValue)
                clips = clips.Where(c => c.ClipType == Type.Value);

            WriteObject(clips.Select(NewClipObject).ToList(), enumerateCollection: true);
        }
    }

    [Cmdlet(VerbsCommon.Add, "ProjectClip", DefaultParameterSetName = "FromBlank", SupportsShouldProcess = true)]
    public sealed class AddProjectClipCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => true;

        // ── 公共参数 ──
        [Parameter(Mandatory = false)]
        public string? Name { get; set; }

        [Parameter(Mandatory = true)]
        public int Track { get; set; }

        [Parameter(Mandatory = false)]
        public double StartX { get; set; } = 0;

        // ── FromBlank ──
        [Parameter(Mandatory = false, ParameterSetName = "FromBlank")]
        public double Width { get; set; } = 300;

        // ── FromFile ──
        [Parameter(Mandatory = true, ParameterSetName = "FromFile")]
        public string? FilePath { get; set; }

        [Parameter(Mandatory = false, ParameterSetName = "FromFile")]
        public uint SourceStart { get; set; } = 0;

        [Parameter(Mandatory = false, ParameterSetName = "FromFile")]
        public uint MaxFrames { get; set; } = 0;

        // ── FromAsset ──
        [Parameter(Mandatory = true, ParameterSetName = "FromAsset")]
        public string? AssetId { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            if (!page!.Tracks.ContainsKey(Track))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Track {Track} does not exist."),
                    "TrackNotFound",
                    ErrorCategory.InvalidArgument,
                    Track));
                return;
            }

            var clipName = Name ?? "New Clip";

            // ── 根据参数集选择创建方式 ──
            switch (ParameterSetName)
            {
                case "FromFile":
                    AddFromFile(page, clipName);
                    break;

                case "FromAsset":
                    AddFromAsset(page, clipName);
                    break;

                default: // FromBlank
                    AddFromBlank(page, clipName);
                    break;
            }
        }

        private void AddFromBlank(DraftPage page, string clipName)
        {
            if (!ShouldProcess($"Track {Track}", $"Add clip '{clipName}'"))
                return;

            try
            {
                var output = InvokeOnUIThread(() =>
                {
                    var clip = page.CreateAndAddClip(StartX, Width, Track,
                        labelText: clipName);
                    return NewClipObject(clip);
                });
                WriteObject(output);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AddClipFailed", ErrorCategory.NotSpecified, null));
            }
        }

        private void AddFromFile(DraftPage page, string clipName)
        {
            if (string.IsNullOrEmpty(FilePath))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("FilePath is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            var fullPath = System.IO.Path.GetFullPath(FilePath);
            if (!System.IO.File.Exists(fullPath))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"File '{fullPath}' does not exist."),
                    "FileNotFound", ErrorCategory.ObjectNotFound, fullPath));
                return;
            }

            if (!ShouldProcess(fullPath, $"Add clip '{clipName}' to track {Track}"))
                return;

            try
            {
                var output = InvokeOnUIThread(() =>
                {
                    var clip = page.CreateAndAddClip(StartX, Width, Track,
                        labelText: clipName,
                        relativeStart: SourceStart,
                        maxFrames: MaxFrames);

                    clip.SourcePath = fullPath;
                    clip.ClipType = ClipElementUI.DetermineClipMode(fullPath);

                    if (clip.ClipType == ClipMode.VideoClip ||
                        clip.ClipType == ClipMode.AudioClip)
                    {
                        clip.UpdateSourceDuration();
                    }

                    return NewClipObject(clip);
                });
                WriteObject(output);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AddClipFromFileFailed", ErrorCategory.NotSpecified, null));
            }
        }

        private void AddFromAsset(DraftPage page, string clipName)
        {
            if (string.IsNullOrEmpty(AssetId))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("AssetId is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            if (!page.Assets.TryGetValue(AssetId, out var asset))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Asset '{AssetId}' not found."),
                    "AssetNotFound", ErrorCategory.ObjectNotFound, AssetId));
                return;
            }

            if (!ShouldProcess(asset.Name ?? AssetId, $"Add clip to track {Track}"))
                return;

            try
            {
                var output = InvokeOnUIThread(() =>
                {
                    var clip = page.CreateFromAsset(asset, Track, StartX);
                    clip.DisplayName = clipName;
                    return NewClipObject(clip);
                });
                WriteObject(output);
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AddClipFromAssetFailed", ErrorCategory.NotSpecified, null));
            }
        }
    }

    [Cmdlet(VerbsCommon.Set, "ProjectClip", SupportsShouldProcess = true)]
    public sealed class SetProjectClipCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0)]
        public Guid Id { get; set; }

        [Parameter]
        public string? Name { get; set; }

        [Parameter]
        public double? StartX { get; set; }

        [Parameter]
        public double? Width { get; set; }

        [Parameter]
        public int? Track { get; set; }

        [Parameter]
        public string? SourcePath { get; set; }

        [Parameter]
        public int? TargetX { get; set; }

        [Parameter]
        public int? TargetY { get; set; }

        [Parameter]
        public int? TargetWidth { get; set; }

        [Parameter]
        public int? TargetHeight { get; set; }

        [Parameter]
        public SwitchParameter PassThru { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var clip = ResolveClip(page!, Id);
            if (clip is null) return;

            if (!ShouldProcess($"Clip '{clip.DisplayName}' ({Id})", "Set properties"))
                return;

            var output = InvokeOnUIThread(() =>
            {
                // 逐个应用提供的参数
                if (Name is not null)
                    clip.DisplayName = Name;

                if (StartX.HasValue)
                {
                    clip.origX = StartX.Value;
                    clip.layoutX = StartX.Value;
                    var absX = page!.FrameToPixel((uint)StartX.Value);
                    clip.Clip.TranslationX = absX;
                }

                if (Width.HasValue)
                {
                    clip.origLength = Width.Value;
                    clip.Clip.WidthRequest = Width.Value;
                }

                if (Track.HasValue && Track.Value != clip.origTrack)
                    MoveToTrack(page!, clip, Track.Value);

                if (SourcePath is not null)
                {
                    clip.SourcePath = System.IO.Path.GetFullPath(SourcePath);
                    var mode = ClipElementUI.DetermineClipMode(clip.SourcePath);
                    if (mode != ClipMode.Special)
                        clip.ClipType = mode;
                }

                if (TargetX.HasValue) clip.TargetX = TargetX.Value;
                if (TargetY.HasValue) clip.TargetY = TargetY.Value;
                if (TargetWidth.HasValue) clip.TargetWidth = TargetWidth.Value;
                if (TargetHeight.HasValue) clip.TargetHeight = TargetHeight.Value;

                return PassThru ? NewClipObject(clip) : null;
            });

            if (output is not null)
                WriteObject(output);
        }

        private static void MoveToTrack(DraftPage page, ClipElementUI clip, int newTrack)
        {
            // 从旧轨道移除
            if (clip.origTrack is not null && page.Tracks.TryGetValue(clip.origTrack.Value, out var oldLayout))
            {
                oldLayout.Children.Remove(clip.Clip);
            }

            // 确保目标轨道存在
            if (!page.Tracks.ContainsKey(newTrack))
                page.AddATrack(newTrack);

            clip.origTrack = newTrack;

            // 添加到新轨道
            page.AddAClip(clip);
        }
    }

    [Cmdlet(VerbsCommon.Remove, "ProjectClip", SupportsShouldProcess = true)]
    public sealed class RemoveProjectClipCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public Guid Id { get; set; }

        [Parameter]
        public SwitchParameter Force { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var clip = ResolveClip(page!, Id);
            if (clip is null) return;

            var action = $"Remove clip '{clip.DisplayName}' from track {clip.origTrack}";
            if (!Force && !ShouldProcess(clip.Id.ToString(), clip.DisplayName, action))
                return;

            try
            {
                InvokeOnUIThread(() =>
                {
                    // 从轨道可视化布局移除
                    if (clip.origTrack is not null &&
                        page!.Tracks.TryGetValue(clip.origTrack.Value, out var trackLayout))
                    {
                        trackLayout.Children.Remove(clip.Clip);
                    }

                    page!.Clips.TryRemove(clip.Id, out _);

                    // 清理引用此 clip 的 Transform
                    var clipIdStr = clip.Id.ToString();
                    var transformsToRemove = page.Clips.Values
                        .Where(c => c.ClipType == ClipMode.TransformClip &&
                                    (c.SourcePath == clipIdStr || c.ExtraData?.ContainsKey(clipIdStr) == true))
                        .ToList();

                    foreach (var t in transformsToRemove)
                    {
                        if (t.origTrack is not null &&
                            page.Tracks.TryGetValue(t.origTrack.Value, out var tLayout))
                        {
                            tLayout.Children.Remove(t.Clip);
                        }
                        page.Clips.TryRemove(t.Id, out _);
                    }
                });
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "RemoveClipFailed", ErrorCategory.NotSpecified, Id));
            }
        }
    }

    [Cmdlet(VerbsCommon.Copy, "ProjectClip", SupportsShouldProcess = true)]
    public sealed class CopyProjectClipCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0)]
        public Guid Id { get; set; }

        [Parameter]
        public int? Track { get; set; }

        [Parameter]
        public double? StartX { get; set; }

        [Parameter]
        public string? Name { get; set; }

        [Parameter]
        public SwitchParameter PassThru { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var source = ResolveClip(page!, Id);
            if (source is null) return;

            var targetTrack = Track ?? source.origTrack ?? 0;
            var targetStartX = StartX ?? source.origX + source.origLength + 10;
            var newName = Name ?? $"Copy of {source.DisplayName}";

            if (!ShouldProcess($"Create copy of '{source.DisplayName}'", "Copy clip"))
                return;

            try
            {
                // 确保目标轨道存在
                if (!page!.Tracks.ContainsKey(targetTrack))
                    page.AddATrack(targetTrack);

                // 创建新 clip
                var newClip = ClipElementUI.CreateClip(
                    targetStartX, source.origLength, targetTrack,
                    id: Guid.NewGuid(),
                    labelText: newName);

                // 复制数据属性
                newClip.ClipType = source.ClipType;
                newClip.SourcePath = source.SourcePath;
                newClip.maxFrameCount = source.maxFrameCount;
                newClip.isInfiniteLength = source.isInfiniteLength;
                newClip.FromPlugin = source.FromPlugin;
                newClip.TypeName = source.TypeName;
                newClip.TargetX = source.TargetX;
                newClip.TargetY = source.TargetY;
                newClip.TargetWidth = source.TargetWidth;
                newClip.TargetHeight = source.TargetHeight;

                // 复制 Effects 和 EffectProviders（浅拷贝）
                if (source.Effects is { Count: > 0 })
                    newClip.Effects = new Dictionary<string, IEffect>(source.Effects);
                if (source.EffectProviders is { Count: > 0 })
                    newClip.EffectProviders = new Dictionary<Guid, IEffectProvider>(source.EffectProviders);

                // 复制 ExtraData
                newClip.ExtraData = new Dictionary<string, object>(source.ExtraData);

                // 注册并添加到时间线
                page.RegisterClip(newClip, resolveOverlap: true);
                page.AddAClip(newClip);

                if (PassThru)
                    WriteObject(NewClipObject(newClip));
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "CopyClipFailed", ErrorCategory.NotSpecified, Id));
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  #region Asset CRUD
    // ════════════════════════════════════════════════════════════════

    [Cmdlet(VerbsCommon.Get, "ProjectAsset")]
    public sealed class GetProjectAssetCommand : DraftPageCmdletBase
    {
        [Parameter]
        public string? Name { get; set; }

        [Parameter]
        public AssetType? Type { get; set; }

        [Parameter]
        public string? AssetId { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            var assets = page!.Assets.Values.AsEnumerable();

            if (!string.IsNullOrEmpty(AssetId))
                assets = assets.Where(a => a.AssetId == AssetId);

            if (!string.IsNullOrEmpty(Name))
            {
                var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(Name)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                assets = assets.Where(a =>
                    System.Text.RegularExpressions.Regex.IsMatch(a.Name ?? "", pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }

            if (Type.HasValue)
                assets = assets.Where(a => a.AssetType == Type.Value);

            WriteObject(assets.Select(NewAssetObject).ToList(), enumerateCollection: true);
        }
    }

    [Cmdlet(VerbsCommon.Add, "ProjectAsset", SupportsShouldProcess = true)]
    public sealed class AddProjectAssetCommand : DraftPageCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string? Name { get; set; }

        [Parameter(Mandatory = true, Position = 1)]
        public string? FilePath { get; set; }

        [Parameter]
        public AssetType? Type { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            if (string.IsNullOrEmpty(Name) || string.IsNullOrEmpty(FilePath))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("Both Name and FilePath are required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            var fullPath = System.IO.Path.GetFullPath(FilePath);
            if (!System.IO.File.Exists(fullPath))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"File '{fullPath}' does not exist."),
                    "FileNotFound", ErrorCategory.ObjectNotFound, fullPath));
                return;
            }

            if (!ShouldProcess(fullPath, $"Add asset '{Name}'"))
                return;

            try
            {
                var assetType = Type ?? AssetItem.GetAssetType(fullPath);
                var assetId = Guid.NewGuid().ToString("N");

                if (!string.IsNullOrWhiteSpace(page?.WorkingPath))
                {
                    var resultPath = Path.Combine(page.WorkingPath, "assets", Guid.NewGuid().ToString() + Path.GetExtension(fullPath));
                    File.Copy(fullPath, resultPath, true);
                    var asset = AssetDatabase.Create(resultPath, Name, AssetItem.GetAssetType(resultPath));
                    if (asset is null)
                    {
                        WriteError(new ErrorRecord(
                            new InvalidOperationException("Failed to create asset, and the source file may be invalid. Try reading the media's info via Get-MediaInfo."),
                            "AssetCreationFailed", ErrorCategory.InvalidData, null));
                        return;
                    }
                    page!.Assets.TryAdd(assetId, asset);
                    WriteObject(NewAssetObject(asset));
                }
                else
                {
                    WriteError(new ErrorRecord(
                        new InvalidOperationException("Project working path is not set. Cannot add asset."),
                        "WorkingPathNotSet", ErrorCategory.InvalidOperation, null));
                }
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AddAssetFailed", ErrorCategory.NotSpecified, null));
            }
        }
    }

    [Cmdlet(VerbsCommon.Remove, "ProjectAsset", SupportsShouldProcess = true)]
    public sealed class RemoveProjectAssetCommand : DraftPageCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public string? AssetId { get; set; }

        [Parameter]
        public SwitchParameter Force { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            if (string.IsNullOrEmpty(AssetId))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("AssetId is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            if (!page!.Assets.TryGetValue(AssetId, out var asset))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Asset '{AssetId}' not found."),
                    "AssetNotFound", ErrorCategory.ObjectNotFound, AssetId));
                return;
            }

            if (!Force && !ShouldProcess(asset.Name ?? AssetId, "Remove asset"))
                return;

            page.Assets.TryRemove(AssetId, out _);
        }
    }


    // ════════════════════════════════════════════════════════════════
    //  #region Track Management
    // ════════════════════════════════════════════════════════════════

    [Cmdlet(VerbsCommon.Get, "ProjectTrack")]
    public sealed class GetProjectTrackCommand : DraftPageCmdletBase
    {
        [Parameter]
        public int? Id { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            var trackIds = Id.HasValue
                ? new[] { Id.Value }.Where(page!.Tracks.ContainsKey)
                : page!.Tracks.Keys.OrderBy(k => k);

            WriteObject(
                trackIds.Select(t => NewTrackObject(page!, t)).ToList(),
                enumerateCollection: true);
        }
    }

    [Cmdlet(VerbsCommon.Add, "ProjectTrack", SupportsShouldProcess = true)]
    public sealed class AddProjectTrackCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter]
        public int? Id { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            var trackId = Id ?? (page!.Tracks.Keys.Any() ? page.Tracks.Keys.Max() + 1 : 0);

            if (page!.Tracks.ContainsKey(trackId))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Track {trackId} already exists."),
                    "TrackAlreadyExists", ErrorCategory.ResourceExists, trackId));
                return;
            }

            if (!ShouldProcess($"Add track {trackId}"))
                return;

            var output = InvokeOnUIThread(() =>
            {
                page.AddATrack(trackId);
                return NewTrackObject(page, trackId);
            });
            WriteObject(output);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  #region Project Info
    // ════════════════════════════════════════════════════════════════

    [Cmdlet(VerbsCommon.Get, "ProjectInfo")]
    public sealed class GetProjectInfoCommand : DraftPageCmdletBase
    {
        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            var pi = page!.ProjectInfo;

            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("ProjectName", pi?.ProjectName ?? "Untitled"));
            obj.Properties.Add(new PSNoteProperty("Width", pi?.RelativeWidth ?? 1920));
            obj.Properties.Add(new PSNoteProperty("Height", pi?.RelativeHeight ?? 1080));
            obj.Properties.Add(new PSNoteProperty("FrameRate", pi?.TargetFrameRate ?? 60));
            obj.Properties.Add(new PSNoteProperty("Duration", page.ProjectDuration));
            obj.Properties.Add(new PSNoteProperty("ClipCount", page.Clips.Count));
            obj.Properties.Add(new PSNoteProperty("TrackCount", page.Tracks.Count));
            obj.Properties.Add(new PSNoteProperty("AssetCount", page.Assets.Count));
            obj.Properties.Add(new PSNoteProperty("WorkingPath", page.WorkingPath ?? ""));
            obj.Properties.Add(new PSNoteProperty("CurrentFrame", page.CurrentFrame));

            WriteObject(obj);
        }
    }
    [Cmdlet(VerbsCommon.Get, "EnvironmentInfo")]
    public sealed class GetEnvironmentInfoCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => false;

        protected override void ProcessRecordImpl()
        {
            var obj = new PSObject();
            obj.Properties.Add(new PSNoteProperty("LoadedPlugins", new PSDataCollection<IPluginBase>(PluginManager.LoadedPlugins.Values)));
            obj.Properties.Add(new PSNoteProperty("TextStyles", new PSDataCollection<ITextClipStyleProvider>(PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(c => c.TextClipStyleProvider).Select(c => c.Value()))));
            obj.Properties.Add(new PSNoteProperty("Effects", new PSDataCollection<IEffectProvider>(PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(c => c.EffectProviderProvider).Select(c => c.Value()))));
            WriteObject(obj);
        }
    }
    [Cmdlet(VerbsCommon.Get, "ScriptWorkspacePath")]
    public sealed class GetScriptWorkspacePathCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => false;

        protected override void ProcessRecordImpl()
        {
            WriteObject(Path.GetFullPath(Path.Combine(FileSystem.CacheDirectory, "ScriptWorkspace")));
        }
    }
}
