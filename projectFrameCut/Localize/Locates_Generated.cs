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
    /// Get the localized string for RenderPage_BackendStatus (like 后端延迟{0}ms, 内存占用{1} MB (后端/此电脑))
    /// </summary>
    public string RenderPage_BackendStatus();
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_MemoryOnly (like 程序已使用内存: {0})
    /// </summary>
    public string RenderPage_BackendStatus_MemoryOnly();
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_NotRespond (like 后端未响应， 内存占用{0} MB (后端/此电脑))
    /// </summary>
    public string RenderPage_BackendStatus_NotRespond();
    
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
    /// Get the localized string for RenderPage_RenderOneFrame (like 渲染第{0}帧 ({1})...)
    /// </summary>
    public string RenderPage_RenderOneFrame();
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderTimeout (like 错误：渲染超时)
    /// </summary>
    public string RenderPage_RenderTimeout { get; }
    
    /// <summary>
    /// Get the localized string for RenderPage_Selected (like 已选中：{0})
    /// </summary>
    public string RenderPage_Selected();
    
    /// <summary>
    /// Get the localized string for RenderPage_Track (like 轨道 #{0})
    /// </summary>
    public string RenderPage_Track();
    
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
                "RenderPage_BackendStatus" => RenderPage_BackendStatus(),
                "RenderPage_BackendStatus_MemoryOnly" => RenderPage_BackendStatus_MemoryOnly(),
                "RenderPage_BackendStatus_NotRespond" => RenderPage_BackendStatus_NotRespond(),
                "RenderPage_RenderOneFrame" => RenderPage_RenderOneFrame(),
                "RenderPage_Selected" => RenderPage_Selected(),
                "RenderPage_Track" => RenderPage_Track(),
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
    string ISimpleLocalizerBase.RenderPage_BackendStatus() => RenderPage_BackendStatus();
    public string RenderPage_BackendStatus() => @$"后端延迟{0}ms, 内存占用{1} MB (后端/此电脑)";
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_MemoryOnly in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus_MemoryOnly() => RenderPage_BackendStatus_MemoryOnly();
    public string RenderPage_BackendStatus_MemoryOnly() => @$"程序已使用内存: {0}";
    
    /// <summary>
    /// Get the localized string for RenderPage_BackendStatus_NotRespond in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_BackendStatus_NotRespond() => RenderPage_BackendStatus_NotRespond();
    public string RenderPage_BackendStatus_NotRespond() => @$"后端未响应， 内存占用{0} MB (后端/此电脑)";
    
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
    string ISimpleLocalizerBase.RenderPage_RenderOneFrame() => RenderPage_RenderOneFrame();
    public string RenderPage_RenderOneFrame() => @$"渲染第{0}帧 ({1})...";
    
    /// <summary>
    /// Get the localized string for RenderPage_RenderTimeout in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_RenderTimeout => RenderPage_RenderTimeout;
    public readonly string RenderPage_RenderTimeout = @"错误：渲染超时";
    
    /// <summary>
    /// Get the localized string for RenderPage_Selected in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Selected() => RenderPage_Selected();
    public string RenderPage_Selected() => @$"已选中：{0}";
    
    /// <summary>
    /// Get the localized string for RenderPage_Track in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.RenderPage_Track() => RenderPage_Track();
    public string RenderPage_Track() => @$"轨道 #{0}";
    
    /// <summary>
    /// Get the localized string for WelcomeMessage in zh-CN
    /// </summary>
    string ISimpleLocalizerBase.WelcomeMessage => WelcomeMessage;
    public readonly string WelcomeMessage = @"欢迎来到projectFrameCut beta!";
    
    /// <summary>
    /// Get the localized string for _LocaleId_ in zh-CN
    /// </summary>
    string ISimpleLocalizerBase._LocaleId_ => _LocaleId_;
    public readonly string _LocaleId_ = @"zh-CN";
    
    
}

    public static class SimpleLocalizerBaseGeneratedHelper 
    {
        /// <summary>
        /// Get the default seted localizer instance
        /// </summary>
        public static ISimpleLocalizerBase Localized { get; set; } = null!;
    }
