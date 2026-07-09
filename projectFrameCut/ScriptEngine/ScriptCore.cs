using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;

namespace projectFrameCut.ScriptEngine
{
    /// <summary>
    /// 基于 PowerShell SDK 的脚本引擎，将 DraftPage 时间线暴露给 PowerShell 脚本。
    /// 提供内置命令 Get-ProjectClip 和 Add-ProjectClip。
    /// DraftPage 实例也作为 $page 变量供高级脚本使用。
    /// </summary>
    public class ScriptCore : IDisposable
    {
        private Runspace? _runspace;

        /// <summary>
        /// 当前绑定的 DraftPage。
        /// </summary>
        public DraftPage? CurrentPage { get; private set; }

        /// <summary>
        /// 获取当前运行空间使用的授权管理器实例。
        /// </summary>
        internal PSCommandAuthorizationHelper? AuthorizationManager { get; private set; }

        /// <summary>
        /// 当前引擎关联的命令筛选器，提供预分析和参数提取。
        /// </summary>
        internal CommandFilter CommandFilter { get; } = new();

        /// <summary>
        /// 待执行的命令参数缓存，由 <see cref="CommandFilter.AnalyzeCommands"/> 填充，
        /// 供 <see cref="PSCommandAuthorizationHelper.ShouldRun"/> 在命令级使用。
        /// 使用 AsyncLocal 确保多线程场景下的隔离。
        /// </summary>
        internal static readonly AsyncLocal<List<CommandParameterInfo>?> PendingCommandParameters = new();

        /// <summary>
        /// 初始化脚本引擎，创建持久的 PowerShell 运行空间并注册内置命令。
        /// </summary>
        /// <param name="page">当前绑定的 DraftPage。</param>
        /// <param name="authHandler">可选的命令授权处理器，用于询问用户授权决策。</param>
        public void Initialize(DraftPage? page = null,
            CommandAuthorizationCallback? authHandler = null,
            EnhancedAuthorizationCallback? enhancedAuthHandler = null)
        {
            try
            {
                Directory.Delete(Path.GetFullPath(Path.Combine(FileSystem.CacheDirectory, "ScriptWorkspace")), true);
            }
            catch { }

            CurrentPage = page;

            // 设置 CommandFilter 的项目路径
            CommandFilter.WorkingPath = page?.WorkingPath;

            // 创建授权管理器并设置可配置的授权处理器
            var auth = new PSCommandAuthorizationHelper(Guid.NewGuid().ToString());
            auth.AuthorizationHandler = authHandler;
            auth.EnhancedAuthorizationHandler = enhancedAuthHandler;
            auth.CommandFilter = CommandFilter;
            AuthorizationManager = auth;

            // 创建自定义的 InitialSessionState，注册所有 Cmdlet
            var iss = InitialSessionState.CreateDefault();
            iss.AuthorizationManager = auth;
            RegisterCmdlets(iss);

            // 创建与应用程序同进程的 PowerShell 运行空间，命令持久化
            _runspace = RunspaceFactory.CreateRunspace(iss);
            _runspace.Open();

            // 将 DraftPage 作为全局变量暴露给 PowerShell 脚本
            _runspace.SessionStateProxy.SetVariable("page", page);
        }

        /// <summary>
        /// 同步执行 PowerShell 脚本并返回格式化输出。
        /// 如果脚本会修改时间线，则必须在 UI 线程上调用。
        /// </summary>
        public string Execute(string script)
        {
            // ---- 预分析 ----
            PreAnalyzeScript(script);

            try
            {
                using var ps = PowerShell.Create(_runspace);
                ps.AddScript(script).AddCommand("Out-String").AddParameter("Width", 4096);
                var results = ps.Invoke();

                if (!results.Any()) //in some cases pwsh command will return nothing, like when you call command like 'cls'
                {
                    return "";
                }
                var output = string.Concat(results.Select(r => r?.ToString() ?? ""));
                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine,
                        ps.Streams.Error.Select(e => { Log(e.Exception, "exec pwsh command", ps); return $"ERROR: {e}"; }));
                    if (!string.IsNullOrEmpty(output))
                        output += Environment.NewLine + "---" + Environment.NewLine;
                    output += errors;
                }
                return output.TrimEnd();
            }
            finally
            {
                PendingCommandParameters.Value = null;
            }
        }

        /// <summary>
        /// 异步执行 PowerShell 脚本并返回格式化输出。
        /// 如果脚本会修改时间线，则必须在 UI 线程上调用。
        /// </summary>
        public async Task<string> ExecuteAsync(string script)
        {
            // ---- 预分析 ----
            PreAnalyzeScript(script);

            try
            {
                using var ps = PowerShell.Create(_runspace);
                ps.AddScript(script).AddCommand("Out-String").AddParameter("Width", 4096);
                var results = await ps.InvokeAsync();

                if (!results.Any()) //in some cases pwsh command will return nothing, like when you call command like 'cls'
                {
                    return "";
                }
                var output = string.Concat(results.Select(r => r?.ToString() ?? ""));
                if (ps.HadErrors)
                {
                    var errors = string.Join(Environment.NewLine,
                        ps.Streams.Error.Select(e => { Log(e.Exception, "exec pwsh command", ps); return $"ERROR: {e}"; }));
                    if (!string.IsNullOrEmpty(output))
                        output += Environment.NewLine + "---" + Environment.NewLine;
                    output += errors;
                }
                return output.TrimEnd();
            }
            finally
            {
                PendingCommandParameters.Value = null;
            }
        }

        /// <summary>
        /// 执行预分析：检测脚本混淆并提取命令参数。
        /// 在 PowerShell 执行前调用，结果通过 <see cref="PendingCommandParameters"/>
        /// 传递给授权管理器。
        /// </summary>
        private void PreAnalyzeScript(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return;

            try
            {
                // 1. 混淆分析
                var analysis = CommandFilter.AnalyzeScript(script);

                if (analysis.ThreatLevel >= ThreatLevel.Critical)
                {
                    throw new InvalidOperationException(
                        $"脚本因检测到危险模式被安全策略阻止：{analysis.Summary}");
                }

                if (analysis.IsSuspicious)
                {
                    Logger.Log($"[CommandFilter] 脚本威胁级别: {analysis.ThreatLevel}, " +
                               $"标记: {string.Join(", ", analysis.Flags)}, " +
                               $"混淆模式: {analysis.Obfuscations.Count} 个");
                }

                // 2. 提取命令参数（路径/URL），供授权管理器使用
                var cmdParams = CommandFilter.AnalyzeCommands(script);
                PendingCommandParameters.Value = cmdParams;
            }
            catch (InvalidOperationException)
            {
                // 重新抛出 Critical 级别的异常
                throw;
            }
            catch (Exception ex)
            {
                // 其他异常（如 Parser 相关）仅记录日志，不影响执行
                Logger.Log(ex, "[CommandFilter] 脚本预分析异常");
            }
        }

        /// <summary>
        /// 更新当前绑定的 DraftPage，自动同步到运行空间变量。
        /// </summary>
        public void SetCurrentPage(DraftPage? page)
        {
            CurrentPage = page;
            if (_runspace?.RunspaceStateInfo.State == RunspaceState.Opened)
                _runspace.SessionStateProxy.SetVariable("page", page);
        }

        /// <summary>
        /// 在脚本运行空间中设置一个 PowerShell 变量，供后续脚本直接使用。
        /// 变量名无需加 <c>$</c> 前缀。
        /// </summary>
        /// <param name="name">变量名。</param>
        /// <param name="value">变量值。</param>
        public void SetVariable(string name, object? value)
        {
            if (_runspace?.RunspaceStateInfo.State == RunspaceState.Opened)
                _runspace.SessionStateProxy.SetVariable(name, value);
        }

        /// <summary>
        /// 批量设置多个 PowerShell 变量。
        /// </summary>
        /// <param name="variables">变量名字典。</param>
        public void SetVariables(IDictionary<string, object?> variables)
        {
            if (_runspace?.RunspaceStateInfo.State != RunspaceState.Opened)
                return;
            foreach (var (key, value) in variables)
                _runspace.SessionStateProxy.SetVariable(key, value);
        }

        public void Dispose()
        {
            _runspace?.Dispose();
            _runspace = null;
        }

        public static List<SessionStateCmdletEntry> InternalCmdlets => new List<SessionStateCmdletEntry>
        {
            // Clip CRUD
            new SessionStateCmdletEntry("Get-ProjectClip", typeof(GetProjectClipCommand), null),
            new SessionStateCmdletEntry("Add-ProjectClip", typeof(AddProjectClipCommand), null),
            new SessionStateCmdletEntry("Set-ProjectClip", typeof(SetProjectClipCommand), null),
            new SessionStateCmdletEntry("Remove-ProjectClip", typeof(RemoveProjectClipCommand), null),
            new SessionStateCmdletEntry("Copy-ProjectClip", typeof(CopyProjectClipCommand), null),

            // Asset CRUD
            new SessionStateCmdletEntry("Get-ProjectAsset", typeof(GetProjectAssetCommand), null),
            new SessionStateCmdletEntry("Add-ProjectAsset", typeof(AddProjectAssetCommand), null),
            new SessionStateCmdletEntry("Remove-ProjectAsset", typeof(RemoveProjectAssetCommand), null),

            // Effect Management
            new SessionStateCmdletEntry("Get-ProjectClipEffect", typeof(GetProjectClipEffectCommand), null),
            new SessionStateCmdletEntry("Add-ProjectClipEffect", typeof(AddProjectClipEffectCommand), null),
            new SessionStateCmdletEntry("Set-ProjectClipEffect", typeof(SetProjectClipEffectCommand), null),
            new SessionStateCmdletEntry("Remove-ProjectClipEffect", typeof(RemoveProjectClipEffectCommand), null),

            // EffectBundle Management
            new SessionStateCmdletEntry("Get-EffectBundleTypes", typeof(GetProjectEffectBundleTypeCommand), null),
            new SessionStateCmdletEntry("Get-ProjectClipEffectBundle", typeof(GetProjectClipEffectBundleCommand), null),
            new SessionStateCmdletEntry("Add-ProjectClipEffectBundle", typeof(AddProjectClipEffectBundleCommand), null),
            new SessionStateCmdletEntry("Set-ProjectClipEffectBundle", typeof(SetProjectClipEffectBundleCommand), null),
            new SessionStateCmdletEntry("Remove-ProjectClipEffectBundle", typeof(RemoveProjectClipEffectBundleCommand), null),
            new SessionStateCmdletEntry("Get-EffectBundleField", typeof(GetEffectBundleFieldCommand), null),

            // Track Management
            new SessionStateCmdletEntry("Get-ProjectTrack", typeof(GetProjectTrackCommand), null),
            new SessionStateCmdletEntry("Add-ProjectTrack", typeof(AddProjectTrackCommand), null),

            // Project Info
            new SessionStateCmdletEntry("Get-ProjectInfo", typeof(GetProjectInfoCommand), null),
            new SessionStateCmdletEntry("Get-EnvironmentInfo", typeof(GetEnvironmentInfoCommand), null),
            new SessionStateCmdletEntry("Get-ScriptWorkspacePath", typeof(GetScriptWorkspacePathCommand), null),

            // Multimedia
            new SessionStateCmdletEntry("Get-MediaInfo", typeof(GetMediaInfoCommand), null),
            new SessionStateCmdletEntry("Get-MediaFrame", typeof(GetMediaFrameCommand), null),
        };

        /// <summary>
        /// 将所有 DraftManager 中的 Cmdlet 注册到 InitialSessionState。
        /// </summary>
        private static void RegisterCmdlets(InitialSessionState iss)
        {
            foreach (var entry in InternalCmdlets)
            {
                iss.Commands.Add(entry);
            }
        }
    }
}
