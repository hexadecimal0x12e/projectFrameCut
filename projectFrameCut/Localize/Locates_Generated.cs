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
    /// Get the localized string for AppBrand (like projectFrameCut(仮))
    /// </summary>
    public string AppBrand { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus (like 后端延迟{lantency.ToString("n2")}ms, 内存占用{menTotalUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑))
    /// </summary>
    public string RenderPage_BackendStatus(double lantency, double menUsed, double menTotalUsed);
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_MemoryOnly (like 程序已使用内存: {menUsed})
    /// </summary>
    public string RenderPage_BackendStatus_MemoryOnly(double menUsed);
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_NotRespond (like 后端未响应， 内存占用{menTotalUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑))
    /// </summary>
    public string RenderPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed);
    
    /// <summary>
    /// Get the localized string for RenderPage_CannotMoveBecauseOfOverlap (like 操作未被应用，因为此操作会导致重叠)
    /// </summary>
    public string RenderPage_CannotMoveBecauseOfOverlap { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_ChangesApplied (like 更改已被应用)
    /// </summary>
    public string RenderPage_ChangesApplied { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_EverythingFine (like 就绪)
    /// </summary>
    public string RenderPage_EverythingFine { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_GoRender (like 去渲染)
    /// </summary>
    public string RenderPage_GoRender { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_Processing (like 处理中...)
    /// </summary>
    public string RenderPage_Processing { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderDone (like 渲染已完成)
    /// </summary>
    public string RenderPage_RenderDone { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderOneFrame (like 渲染第{frameIndex}帧 ({playheadSeconds.ToString("mm\\:ss\\.ff")})...)
    /// </summary>
    public string RenderPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds);
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderTimeout (like 错误：渲染超时)
    /// </summary>
    public string RenderPage_RenderTimeout { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_Selected (like 已选中：{clipName})
    /// </summary>
    public string RenderPage_Selected(string clipName);
    
    /// <summary>
    /// Get the localized string for RenderPage_Track (like 轨道 #{trackId})
    /// </summary>
    public string RenderPage_Track(int trackId);
    
    /// <summary>
    /// Get the localized string for WelcomeMessage (like 欢迎来到projectFrameCut beta!)
    /// </summary>
    public string WelcomeMessage { get; }
    
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
            "AppBrand" => AppBrand,
            "RenderPage_CannotMoveBecauseOfOverlap" => RenderPage_CannotMoveBecauseOfOverlap,
            "RenderPage_ChangesApplied" => RenderPage_ChangesApplied,
            "RenderPage_EverythingFine" => RenderPage_EverythingFine,
            "RenderPage_GoRender" => RenderPage_GoRender,
            "RenderPage_Processing" => RenderPage_Processing,
            "RenderPage_RenderDone" => RenderPage_RenderDone,
            "RenderPage_RenderTimeout" => RenderPage_RenderTimeout,
            "WelcomeMessage" => WelcomeMessage,
            _ => throw new KeyNotFoundException($"Can't find the localized string for id '{id}'")
            };
        }
        public string DynamicLookupWithArgs(string id, params object[] args)
        {
            return id switch
            {
                "RenderPage_BackendStatus" => RenderPage_BackendStatus((double)args[0], (double)args[1], (double)args[2]),
                "RenderPage_BackendStatus_MemoryOnly" => RenderPage_BackendStatus_MemoryOnly((double)args[0]),
                "RenderPage_BackendStatus_NotRespond" => RenderPage_BackendStatus_NotRespond((double)args[0], (double)args[1]),
                "RenderPage_RenderOneFrame" => RenderPage_RenderOneFrame((int)args[0], (TimeSpan)args[1]),
                "RenderPage_Selected" => RenderPage_Selected((string)args[0]),
                "RenderPage_Track" => RenderPage_Track((int)args[0]),
                _ => throw new KeyNotFoundException($"Can't find the localized string for id '{id}' with any argument")
            };
        }
    }
    
[GeneratedCodeAttribute("SimpleLocalizer", "1.0.0.0")]
[DebuggerNonUserCode()]
public class _SimpleLocalizer_zh_CN : ISimpleLocalizerBase
{
    /// <summary>
    /// Get the localized string for AppBrand in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.AppBrand => AppBrand;
    public readonly string AppBrand = @"projectFrameCut(仮)";
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => RenderPage_BackendStatus(lantency,menUsed,menTotalUsed);
    public string RenderPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => @$"后端延迟{lantency.ToString("n2")}ms, 内存占用{menTotalUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑)";
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_MemoryOnly in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus_MemoryOnly(double menUsed) => RenderPage_BackendStatus_MemoryOnly(menUsed);
    public string RenderPage_BackendStatus_MemoryOnly(double menUsed) => @$"程序已使用内存: {menUsed}";
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_NotRespond in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => RenderPage_BackendStatus_NotRespond(menUsed,menTotalUsed);
    public string RenderPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => @$"后端未响应， 内存占用{menTotalUsed.ToString("n2").Replace(',', '\0')}/{menTotalUsed.ToString("n2").Replace(',', '\0')} MB (后端/此电脑)";
    
    /// <summary>
    /// Get the localized string for RenderPage_CannotMoveBecauseOfOverlap in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_CannotMoveBecauseOfOverlap => RenderPage_CannotMoveBecauseOfOverlap;
    public readonly string RenderPage_CannotMoveBecauseOfOverlap = @"操作未被应用，因为此操作会导致重叠";
    
    /// <summary>
    /// Get the localized string for RenderPage_ChangesApplied in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_ChangesApplied => RenderPage_ChangesApplied;
    public readonly string RenderPage_ChangesApplied = @"更改已被应用";
    
    /// <summary>
    /// Get the localized string for RenderPage_EverythingFine in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_EverythingFine => RenderPage_EverythingFine;
    public readonly string RenderPage_EverythingFine = @"就绪";
    
    /// <summary>
    /// Get the localized string for RenderPage_GoRender in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_GoRender => RenderPage_GoRender;
    public readonly string RenderPage_GoRender = @"去渲染";
    
    /// <summary>
    /// Get the localized string for RenderPage_Processing in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Processing => RenderPage_Processing;
    public readonly string RenderPage_Processing = @"处理中...";
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderDone in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_RenderDone => RenderPage_RenderDone;
    public readonly string RenderPage_RenderDone = @"渲染已完成";
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderOneFrame in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => RenderPage_RenderOneFrame(frameIndex,playheadSeconds);
    public string RenderPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => @$"渲染第{frameIndex}帧 ({playheadSeconds.ToString("mm\\:ss\\.ff")})...";
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderTimeout in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_RenderTimeout => RenderPage_RenderTimeout;
    public readonly string RenderPage_RenderTimeout = @"错误：渲染超时";
    
    /// <summary>
    /// Get the localized string for RenderPage_Selected in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Selected(string clipName) => RenderPage_Selected(clipName);
    public string RenderPage_Selected(string clipName) => @$"已选中：{clipName}";
    
    /// <summary>
    /// Get the localized string for RenderPage_Track in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Track(int trackId) => RenderPage_Track(trackId);
    public string RenderPage_Track(int trackId) => @$"轨道 #{trackId}";
    
    /// <summary>
    /// Get the localized string for WelcomeMessage in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.WelcomeMessage => WelcomeMessage;
    public readonly string WelcomeMessage = @"欢迎来到projectFrameCut beta!";
    
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
    /// Get the localized string for RenderPage_BackendStatus in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => RenderPage_BackendStatus(lantency,menUsed,menTotalUsed);
    public string RenderPage_BackendStatus(double lantency, double menUsed, double menTotalUsed) => @$"";
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_MemoryOnly in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus_MemoryOnly(double menUsed) => RenderPage_BackendStatus_MemoryOnly(menUsed);
    public string RenderPage_BackendStatus_MemoryOnly(double menUsed) => @$"";
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_NotRespond in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => RenderPage_BackendStatus_NotRespond(menUsed,menTotalUsed);
    public string RenderPage_BackendStatus_NotRespond(double menUsed, double menTotalUsed) => @$"";
    
    /// <summary>
    /// Get the localized string for RenderPage_CannotMoveBecauseOfOverlap in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_CannotMoveBecauseOfOverlap => RenderPage_CannotMoveBecauseOfOverlap;
    public readonly string RenderPage_CannotMoveBecauseOfOverlap = @"";
    
    /// <summary>
    /// Get the localized string for RenderPage_ChangesApplied in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_ChangesApplied => RenderPage_ChangesApplied;
    public readonly string RenderPage_ChangesApplied = @"";
    
    /// <summary>
    /// Get the localized string for RenderPage_EverythingFine in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_EverythingFine => RenderPage_EverythingFine;
    public readonly string RenderPage_EverythingFine = @"";
    
    /// <summary>
    /// Get the localized string for RenderPage_GoRender in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_GoRender => RenderPage_GoRender;
    public readonly string RenderPage_GoRender = @"";
    
    /// <summary>
    /// Get the localized string for RenderPage_Processing in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Processing => RenderPage_Processing;
    public readonly string RenderPage_Processing = @"";
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderDone in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_RenderDone => RenderPage_RenderDone;
    public readonly string RenderPage_RenderDone = @"";
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderOneFrame in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => RenderPage_RenderOneFrame(frameIndex,playheadSeconds);
    public string RenderPage_RenderOneFrame(int frameIndex, TimeSpan playheadSeconds) => @$"";
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderTimeout in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_RenderTimeout => RenderPage_RenderTimeout;
    public readonly string RenderPage_RenderTimeout = @"";
    
    /// <summary>
    /// Get the localized string for RenderPage_Selected in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Selected(string clipName) => RenderPage_Selected(clipName);
    public string RenderPage_Selected(string clipName) => @$"";
    
    /// <summary>
    /// Get the localized string for RenderPage_Track in en-US
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Track(int trackId) => RenderPage_Track(trackId);
    public string RenderPage_Track(int trackId) => @$"";
    
    /// <summary>
    /// Get the localized string for WelcomeMessage in en-US
    /// </summary>
    string ISimpleLocalizerBase.WelcomeMessage => WelcomeMessage;
    public readonly string WelcomeMessage = @"";
    
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
