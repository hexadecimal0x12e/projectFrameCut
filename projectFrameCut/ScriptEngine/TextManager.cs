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
    /// <summary>
    /// 获取指定文本样式类型的所有 SettableFields 定义。
    /// </summary>
    [Cmdlet("Get", "TextStyleField")]
    public sealed class GetTextStyleFieldCommand : DraftPageCmdletBase
    {
        [Parameter(Mandatory = true, Position = 0)]
        public string? StyleId { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (string.IsNullOrWhiteSpace(StyleId))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("StyleId is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            var provider = TimelineMcpLiveService.ResolveTextStyleProvider(StyleId);
            if (provider is null)
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Text style '{StyleId}' not found."),
                    "TextStyleNotFound",
                    ErrorCategory.ObjectNotFound,
                    StyleId));
                return;
            }

            if (provider.SettableFields is null || provider.SettableFields.Count == 0)
            {
                WriteWarning($"Text style '{StyleId}' has no settable fields.");
                return;
            }

            WriteObject(
                provider.SettableFields.Values.Select(f => new PSObject(new
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
                })).ToList(),
                enumerateCollection: true);
        }
    }

    /// <summary>
    /// 添加一个文本 Clip，并可通过 -Fields 初始化 SettableFields。
    /// </summary>
    [Cmdlet(VerbsCommon.Add, "ProjectTextClip", SupportsShouldProcess = true)]
    public sealed class AddProjectTextClipCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0)]
        public string? StyleId { get; set; }

        [Parameter(Mandatory = true, Position = 1)]
        public string? Text { get; set; }

        [Parameter(Mandatory = true, Position = 2)]
        public int Track { get; set; }

        [Parameter]
        public int StartX { get; set; }

        [Parameter]
        public Hashtable? Fields { get; set; }

        [Parameter]
        public SwitchParameter PassThru { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;

            if (string.IsNullOrWhiteSpace(StyleId))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("StyleId is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            if (string.IsNullOrWhiteSpace(Text))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException("Text is required."),
                    "InvalidArgument", ErrorCategory.InvalidArgument, null));
                return;
            }

            if (!page!.Tracks.ContainsKey(Track))
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Track {Track} does not exist."),
                    "TrackNotFound",
                    ErrorCategory.InvalidArgument,
                    Track));
                return;
            }

            if (TimelineMcpLiveService.ResolveTextStyleProvider(StyleId) is null)
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Text style '{StyleId}' not found."),
                    "TextStyleNotFound",
                    ErrorCategory.ObjectNotFound,
                    StyleId));
                return;
            }

            if (!ShouldProcess($"Track {Track}", $"Add text clip '{Text}' with style '{StyleId}'"))
                return;

            try
            {
                var fields = Fields?.Cast<DictionaryEntry>()
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key?.ToString()))
                    .ToDictionary(e => e.Key!.ToString()!, e => e.Value ?? new object());

                var element = TimelineMcpLiveService.AddTextClipToPage(page!, StyleId, Text, StartX, Track, fields);

                if (PassThru)
                    WriteObject(NewClipObject(element));
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "AddTextClipFailed", ErrorCategory.NotSpecified, null));
            }
        }
    }

    /// <summary>
    /// 修改现有文本 Clip 的 SettableFields。
    /// </summary>
    [Cmdlet(VerbsCommon.Set, "ProjectTextClipStyle", SupportsShouldProcess = true)]
    public sealed class SetProjectTextClipStyleCommand : DraftPageCmdletBase
    {
        protected override bool RequiresUIThread => true;

        [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
        public Guid Id { get; set; }

        [Parameter(Mandatory = true)]
        public Hashtable Fields { get; set; } = new Hashtable();

        [Parameter]
        public SwitchParameter PassThru { get; set; }

        protected override void ProcessRecordImpl()
        {
            if (!EnsurePageLoaded(out var page)) return;
            var clip = ResolveClip(page!, Id);
            if (clip is null) return;

            if (clip.ClipType != ClipMode.TextClip)
            {
                WriteError(new ErrorRecord(
                    new ArgumentException($"Clip '{Id}' is not a text clip."),
                    "NotATextClip",
                    ErrorCategory.InvalidArgument,
                    Id));
                return;
            }

            if (Fields is null || Fields.Count == 0)
            {
                WriteWarning("No fields provided. Use -Fields to specify settable fields.");
                return;
            }

            if (!ShouldProcess($"Clip '{clip.DisplayName}' ({Id})", "Set text clip style fields"))
                return;

            try
            {
                var fields = Fields.Cast<DictionaryEntry>()
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key?.ToString()))
                    .ToDictionary(e => e.Key!.ToString()!, e => e.Value ?? new object());

                var resultLog = TimelineMcpLiveService.SetTextClipStyleFields(page!, Id, fields);

                foreach (var log in resultLog)
                {
                    if (log.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
                        WriteWarning(log);
                }

                if (PassThru)
                    WriteObject(NewClipObject(clip));
            }
            catch (Exception ex)
            {
                WriteError(new ErrorRecord(ex, "SetTextClipStyleFailed", ErrorCategory.NotSpecified, Id));
            }
        }
    }
}
