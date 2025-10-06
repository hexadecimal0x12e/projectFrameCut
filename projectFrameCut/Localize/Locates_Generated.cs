using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Reflection;

using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Reflection;
[GeneratedCodeAttribute("SimpleLocalizer", "1.0.0.0")]
public interface ISimpleLocalizerBase
{
    /// <summary>
    /// Get the localized string for _Info (like 提示)
    /// </summary>
    public string _Info { get; }
    
    /// <summary>
    /// Get the localized string for _OK (like 好的)
    /// </summary>
    public string _OK { get; }
    
    /// <summary>
    /// Get the localized string for AppBrand (like projectFrameCut(仮))
    /// </summary>
    public string AppBrand { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus (like 后端延迟{lantency.ToString("n2")}ms, 内存占用{menUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑))
    /// </summary>
    public string DraftPage_BackendStatus(double lantency, double menUsed, double menTotalUsed);
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus_MemoryOnly (like 程序已使用内存: {menUsed})
    /// </summary>
    public string DraftPage_BackendStatus_MemoryOnly(double menUsed);
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus_NotRespond (like 后端未响应， 内存占用{menTotalUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑))
    /// </summary>
    public string DraftPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed);
    
    /// <summary>
    /// Get the localized string for DraftPage_CannotMoveBecauseOfOverlap (like 操作未被应用，因为此操作会导致重叠)
    /// </summary>
    public string DraftPage_CannotMoveBecauseOfOverlap { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_ChangesApplied (like 更改已被应用)
    /// </summary>
    public string DraftPage_ChangesApplied { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_EverythingFine (like 就绪)
    /// </summary>
    public string DraftPage_EverythingFine { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_GoRender (like 去渲染)
    /// </summary>
    public string DraftPage_GoRender { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_Processing (like 处理中...)
    /// </summary>
    public string DraftPage_Processing { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderDone (like 渲染已完成)
    /// </summary>
    public string DraftPage_RenderDone { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderOneFrame (like 渲染第{frameIndex}帧 ({playheadSeconds.ToString("mm\\:ss\\.ff")})...)
    /// </summary>
    public string DraftPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds);
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderTimeout (like 错误：渲染超时)
    /// </summary>
    public string DraftPage_RenderTimeout { get; }
    
    /// <summary>
    /// Get the localized string for DraftPage_Selected (like 已选中：{clipName})
    /// </summary>
    public string DraftPage_Selected(string clipName);
    
    /// <summary>
    /// Get the localized string for DraftPage_Track (like 轨道 #{trackId})
    /// </summary>
    public string DraftPage_Track(int trackId);
    
    /// <summary>
    /// Get the localized string for RenderPage_ExportTitle (like 导出项目 ｢{name}｣ )
    /// </summary>
    public string RenderPage_ExportTitle(string name);
    
    /// <summary>
    /// Get the localized string for RenderPage_NoDraft (like 你需要先打开一个草稿才能进行渲染)
    /// </summary>
    public string RenderPage_NoDraft { get; }
    
    /// <summary>
    /// Get the localized string for WelcomeMessage (like 欢迎来到projectFrameCut beta!)
    /// </summary>
    public string WelcomeMessage { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_CustomOption (like 自定义)
    /// </summary>
    public string RenderPage_CustomOption { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectResolution (like 选择一个分辨率)
    /// </summary>
    public string RenderPage_SelectResolution { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectFrameRate (like 选择帧率)
    /// </summary>
    public string RenderPage_SelectFrameRate { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectEncoding (like 选择编码)
    /// </summary>
    public string RenderPage_SelectEncoding { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_MaxParallelThreadsCount (like 并发数: {num})
    /// </summary>
    public string RenderPage_MaxParallelThreadsCount(int num);
    
    /// <summary>
    /// Get the current Locale's ID (like 'zh-CN')
    /// </summary>
    public string _LocaleId_ { get; }
    
    public static Dictionary<string, ISimpleLocalizerBase> GetMapping()
    {
        return new Dictionary<string, ISimpleLocalizerBase>()
        {
            { "zh-CN", new _SimpleLocalizer_zh_CN() },
            { "en-US", new _SimpleLocalizer_en_US() },
        };
    }
    public string DynamicLookup(string id)
    {
        return id switch
        {
            "_Info" => _Info,
            "_OK" => _OK,
            "AppBrand" => AppBrand,
            "DraftPage_CannotMoveBecauseOfOverlap" => DraftPage_CannotMoveBecauseOfOverlap,
            "DraftPage_ChangesApplied" => DraftPage_ChangesApplied,
            "DraftPage_EverythingFine" => DraftPage_EverythingFine,
            "DraftPage_GoRender" => DraftPage_GoRender,
            "DraftPage_Processing" => DraftPage_Processing,
            "DraftPage_RenderDone" => DraftPage_RenderDone,
            "DraftPage_RenderTimeout" => DraftPage_RenderTimeout,
            "RenderPage_NoDraft" => RenderPage_NoDraft,
            "WelcomeMessage" => WelcomeMessage,
            "RenderPage_CustomOption" => RenderPage_CustomOption,
            "RenderPage_SelectResolution" => RenderPage_SelectResolution,
            "RenderPage_SelectFrameRate" => RenderPage_SelectFrameRate,
            "RenderPage_SelectEncoding" => RenderPage_SelectEncoding,
            _ => throw new KeyNotFoundException($"Can't find the localized string for id '{id}'")
            };
        }
        public string DynamicLookupWithArgs(string id, params object[] args)
        {
            return id switch
            {
                "DraftPage_BackendStatus" => DraftPage_BackendStatus((double)args[0], (double)args[1], (double)args[2]),
                "DraftPage_BackendStatus_MemoryOnly" => DraftPage_BackendStatus_MemoryOnly((double)args[0]),
                "DraftPage_BackendStatus_NotRespond" => DraftPage_BackendStatus_NotRespond((double)args[0], (double)args[1]),
                "DraftPage_RenderOneFrame" => DraftPage_RenderOneFrame((int)args[0], (TimeSpan)args[1]),
                "DraftPage_Selected" => DraftPage_Selected((string)args[0]),
                "DraftPage_Track" => DraftPage_Track((int)args[0]),
                "RenderPage_ExportTitle" => RenderPage_ExportTitle((string)args[0]),
                "RenderPage_MaxParallelThreadsCount" => RenderPage_MaxParallelThreadsCount((int)args[0]),
                _ => throw new KeyNotFoundException($"Can't find the localized string for id '{id}' with any argument")
            };
        }
    }
    
[GeneratedCodeAttribute("SimpleLocalizer", "1.0.0.0")]
[DebuggerNonUserCode()]
public class _SimpleLocalizer_zh_CN : ISimpleLocalizerBase
{
    /// <summary>
    /// Get the localized string for _Info in zh-CN
    /// </summary>
    string ISimpleLocalizerBase._Info => _Info;
    public readonly string _Info = @"提示";
    
    /// <summary>
    /// Get the localized string for _OK in zh-CN
    /// </summary>
    string ISimpleLocalizerBase._OK => _OK;
    public readonly string _OK = @"好的";
    
    /// <summary>
    /// Get the localized string for AppBrand in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.AppBrand => AppBrand;
    public readonly string AppBrand = @"projectFrameCut(仮)";
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => DraftPage_BackendStatus(lantency,menUsed,menTotalUsed);
    public string DraftPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => @$"后端延迟{lantency.ToString("n2")}ms, 内存占用{menUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑)";
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus_MemoryOnly in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_BackendStatus_MemoryOnly(double menUsed) => DraftPage_BackendStatus_MemoryOnly(menUsed);
    public string DraftPage_BackendStatus_MemoryOnly(double menUsed) => @$"程序已使用内存: {menUsed}";
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus_NotRespond in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => DraftPage_BackendStatus_NotRespond(menUsed,menTotalUsed);
    public string DraftPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => @$"后端未响应， 内存占用{menTotalUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑)";
    
    /// <summary>
    /// Get the localized string for DraftPage_CannotMoveBecauseOfOverlap in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_CannotMoveBecauseOfOverlap => DraftPage_CannotMoveBecauseOfOverlap;
    public readonly string DraftPage_CannotMoveBecauseOfOverlap = @"操作未被应用，因为此操作会导致重叠";
    
    /// <summary>
    /// Get the localized string for DraftPage_ChangesApplied in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_ChangesApplied => DraftPage_ChangesApplied;
    public readonly string DraftPage_ChangesApplied = @"更改已被应用";
    
    /// <summary>
    /// Get the localized string for DraftPage_EverythingFine in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_EverythingFine => DraftPage_EverythingFine;
    public readonly string DraftPage_EverythingFine = @"就绪";
    
    /// <summary>
    /// Get the localized string for DraftPage_GoRender in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_GoRender => DraftPage_GoRender;
    public readonly string DraftPage_GoRender = @"去渲染";
    
    /// <summary>
    /// Get the localized string for DraftPage_Processing in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_Processing => DraftPage_Processing;
    public readonly string DraftPage_Processing = @"处理中...";
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderDone in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_RenderDone => DraftPage_RenderDone;
    public readonly string DraftPage_RenderDone = @"渲染已完成";
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderOneFrame in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => DraftPage_RenderOneFrame(frameIndex,playheadSeconds);
    public string DraftPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => @$"渲染第{frameIndex}帧 ({playheadSeconds.ToString("mm\\:ss\\.ff")})...";
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderTimeout in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_RenderTimeout => DraftPage_RenderTimeout;
    public readonly string DraftPage_RenderTimeout = @"错误：渲染超时";
    
    /// <summary>
    /// Get the localized string for DraftPage_Selected in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_Selected(string clipName) => DraftPage_Selected(clipName);
    public string DraftPage_Selected(string clipName) => @$"已选中：{clipName}";
    
    /// <summary>
    /// Get the localized string for DraftPage_Track in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_Track(int trackId) => DraftPage_Track(trackId);
    public string DraftPage_Track(int trackId) => @$"轨道 #{trackId}";
    
    /// <summary>
    /// Get the localized string for RenderPage_ExportTitle in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_ExportTitle(string name) => RenderPage_ExportTitle(name);
    public string RenderPage_ExportTitle(string name) => @$"导出项目 ｢{name}｣ ";
    
    /// <summary>
    /// Get the localized string for RenderPage_NoDraft in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_NoDraft => RenderPage_NoDraft;
    public readonly string RenderPage_NoDraft = @"你需要先打开一个草稿才能进行渲染";
    
    /// <summary>
    /// Get the localized string for WelcomeMessage in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.WelcomeMessage => WelcomeMessage;
    public readonly string WelcomeMessage = @"欢迎来到projectFrameCut beta!";
    
    /// <summary>
    /// Get the localized string for RenderPage_CustomOption in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_CustomOption => RenderPage_CustomOption;
    public readonly string RenderPage_CustomOption = @"自定义";
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectResolution in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_SelectResolution => RenderPage_SelectResolution;
    public readonly string RenderPage_SelectResolution = @"选择一个分辨率";
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectFrameRate in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_SelectFrameRate => RenderPage_SelectFrameRate;
    public readonly string RenderPage_SelectFrameRate = @"选择帧率";
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectEncoding in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_SelectEncoding => RenderPage_SelectEncoding;
    public readonly string RenderPage_SelectEncoding = @"选择编码";
    
    /// <summary>
    /// Get the localized string for RenderPage_MaxParallelThreadsCount in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_MaxParallelThreadsCount(int num) => RenderPage_MaxParallelThreadsCount(num);
    public string RenderPage_MaxParallelThreadsCount(int num) => @$"并发数: {num}";
    
    /// <summary>
    /// Get the current localized Id (like zh-CN)
    /// </summary>
    string ISimpleLocalizerBase._LocaleId_ => _LocaleId_;
    public readonly string _LocaleId_ = @"zh-CN";
    
    
}

[GeneratedCodeAttribute("SimpleLocalizer", "1.0.0.0")]
[DebuggerNonUserCode()]
public class _SimpleLocalizer_en_US : ISimpleLocalizerBase
{
    /// <summary>
    /// Get the localized string for AppBrand in en-US
    /// </summary>
    string ISimpleLocalizerBase.AppBrand => AppBrand;
    public readonly string AppBrand = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => DraftPage_BackendStatus(lantency,menUsed,menTotalUsed);
    public string DraftPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => @$"";
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus_MemoryOnly in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_BackendStatus_MemoryOnly(double menUsed) => DraftPage_BackendStatus_MemoryOnly(menUsed);
    public string DraftPage_BackendStatus_MemoryOnly(double menUsed) => @$"";
    
    /// <summary>
    /// Get the localized string for DraftPage_BackendStatus_NotRespond in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => DraftPage_BackendStatus_NotRespond(menUsed,menTotalUsed);
    public string DraftPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => @$"";
    
    /// <summary>
    /// Get the localized string for DraftPage_CannotMoveBecauseOfOverlap in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_CannotMoveBecauseOfOverlap => DraftPage_CannotMoveBecauseOfOverlap;
    public readonly string DraftPage_CannotMoveBecauseOfOverlap = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_ChangesApplied in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_ChangesApplied => DraftPage_ChangesApplied;
    public readonly string DraftPage_ChangesApplied = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_EverythingFine in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_EverythingFine => DraftPage_EverythingFine;
    public readonly string DraftPage_EverythingFine = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_GoRender in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_GoRender => DraftPage_GoRender;
    public readonly string DraftPage_GoRender = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_Processing in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_Processing => DraftPage_Processing;
    public readonly string DraftPage_Processing = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderDone in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_RenderDone => DraftPage_RenderDone;
    public readonly string DraftPage_RenderDone = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderOneFrame in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => DraftPage_RenderOneFrame(frameIndex,playheadSeconds);
    public string DraftPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => @$"";
    
    /// <summary>
    /// Get the localized string for DraftPage_RenderTimeout in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_RenderTimeout => DraftPage_RenderTimeout;
    public readonly string DraftPage_RenderTimeout = @"";
    
    /// <summary>
    /// Get the localized string for DraftPage_Selected in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_Selected(string clipName) => DraftPage_Selected(clipName);
    public string DraftPage_Selected(string clipName) => @$"";
    
    /// <summary>
    /// Get the localized string for DraftPage_Track in en-US
    /// </summary>
    string ISimpleLocalizerBase.DraftPage_Track(int trackId) => DraftPage_Track(trackId);
    public string DraftPage_Track(int trackId) => @$"";
    
    /// <summary>
    /// Get the localized string for WelcomeMessage in en-US
    /// </summary>
    string ISimpleLocalizerBase.WelcomeMessage => WelcomeMessage;
    public readonly string WelcomeMessage = @"";
    
    /// <summary>
    /// Get the localized string for _Info in en-US
    /// </summary>
    string ISimpleLocalizerBase._Info => _Info;
    public readonly string _Info = @"Unset localization item:_Info()";
    
    /// <summary>
    /// Get the localized string for _OK in en-US
    /// </summary>
    string ISimpleLocalizerBase._OK => _OK;
    public readonly string _OK = @"Unset localization item:_OK()";
    
    /// <summary>
    /// Get the localized string for RenderPage_ExportTitle in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_ExportTitle(string name) => RenderPage_ExportTitle(name);
    public string RenderPage_ExportTitle(string name) => @$"Unset localization item:RenderPage_ExportTitle(string name)";
    
    /// <summary>
    /// Get the localized string for RenderPage_NoDraft in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_NoDraft => RenderPage_NoDraft;
    public readonly string RenderPage_NoDraft = @"Unset localization item:RenderPage_NoDraft()";
    
    /// <summary>
    /// Get the localized string for RenderPage_CustomOption in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_CustomOption => RenderPage_CustomOption;
    public readonly string RenderPage_CustomOption = @"Unset localization item:RenderPage_CustomOption()";
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectResolution in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_SelectResolution => RenderPage_SelectResolution;
    public readonly string RenderPage_SelectResolution = @"Unset localization item:RenderPage_SelectResolution()";
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectFrameRate in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_SelectFrameRate => RenderPage_SelectFrameRate;
    public readonly string RenderPage_SelectFrameRate = @"Unset localization item:RenderPage_SelectFrameRate()";
    
    /// <summary>
    /// Get the localized string for RenderPage_SelectEncoding in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_SelectEncoding => RenderPage_SelectEncoding;
    public readonly string RenderPage_SelectEncoding = @"Unset localization item:RenderPage_SelectEncoding()";
    
    /// <summary>
    /// Get the localized string for RenderPage_MaxParallelThreadsCount in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_MaxParallelThreadsCount(int num) => RenderPage_MaxParallelThreadsCount(num);
    public string RenderPage_MaxParallelThreadsCount(int num) => @$"Unset localization item:RenderPage_MaxParallelThreadsCount(int num)";
    
    /// <summary>
    /// Get the current localized Id (like zh-CN)
    /// </summary>
    string ISimpleLocalizerBase._LocaleId_ => _LocaleId_;
    public readonly string _LocaleId_ = @"en-US";
    
    
}

namespace LocalizedResources
{
    public static class SimpleLocalizerBaseGeneratedHelper 
{
    /// <summary>
    /// Get the default seted localizer instance
    /// </summary>
    public static ISimpleLocalizerBase Localized { get; set; } = null!;
}
}
