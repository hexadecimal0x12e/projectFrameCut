using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading;
using projectFrameCut.DraftStuff;
using projectFrameCut.Setting.SettingManager;
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
        public static bool Enabled { get; set; } = true;

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
        /// 授权请求事件。宿主订阅此事件以异步处理授权请求。
        /// 宿主通过 <see cref="AuthorizationRequestedEventArgs.Completion"/> TCS 返回决策结果。
        /// </summary>
        public event EventHandler<AuthorizationRequestedEventArgs>? AuthorizationRequested;

        /// <summary>
        /// 预分析阶段存储的脚本混淆警告（如果有），由 <see cref="PreAuthorizeScriptAsync"/> 使用。
        /// </summary>
        private string? _authWarning;

        /// <summary>
        /// 预分析阶段存储的脚本威胁级别（如果有）。
        /// </summary>
        private ThreatLevel? _authThreatLevel;

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
                Directory.Delete(Path.GetFullPath(Path.Combine(MauiProgram.CachePath, "ScriptWorkspace")), true);
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

            // 初始化审计模式
            PSCommandAuthorizationHelper.AuditMode = SettingsManager.IsBoolSettingTrueOrDefault("Security_Script_AuditMode", false);

            // 将 DraftPage 作为全局变量暴露给 PowerShell 脚本。
            // $page 变量的访问受 Security_Script_AllowAccessPageObject 策略控制：
            // 策略为 false 时，脚本预分析会阻止用户脚本访问 $page，
            // 但内部 cmdlet 仍可通过该变量获取页面对象以正常工作。
            if (page != null)
            {
                _runspace.SessionStateProxy.SetVariable("page", page);
            }
        }

        /// <summary>
        /// 同步执行 PowerShell 脚本并返回格式化输出。
        /// 如果脚本会修改时间线，则必须在 UI 线程上调用。
        /// </summary>
        public string Execute(string script)
        {
            // ---- 安全检查：是否允许执行脚本 ----
            if (!SettingsManager.IsBoolSettingTrueOrDefault("Security_EnableScript", true) || !Enabled)
                throw new InvalidOperationException(Localized.ScriptEngine_ScriptDisabled);

            // ---- 安全检查：$page 对象访问控制 ----
            if (!SettingsManager.IsBoolSettingTrueOrDefault("Security_Script_AllowAccessPageObject", false)
                && CommandFilter.HasPageVariableAccess(script))
            {
                throw new NotAllowedCommandException(
                    NotAllowedCommandException.DeniedReason.DisallowedByRuleOfUser,
                    Localized.ScriptEngine_PageAccessDisabled);
            }

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
            // ---- 安全检查：是否允许执行脚本 ----
            if (!SettingsManager.IsBoolSettingTrueOrDefault("Security_EnableScript", true) || !Enabled)
                throw new InvalidOperationException(Localized.ScriptEngine_ScriptDisabled);

            // ---- 安全检查：$page 对象访问控制 ----
            if (!SettingsManager.IsBoolSettingTrueOrDefault("Security_Script_AllowAccessPageObject", false)
                && CommandFilter.HasPageVariableAccess(script))
            {
                throw new NotAllowedCommandException(
                    NotAllowedCommandException.DeniedReason.DisallowedByRuleOfUser,
                    Localized.ScriptEngine_PageAccessDisabled);
            }

            // ---- 预分析 + 预授权 ----
            // PreAuthorizeScriptAsync 内部调用 PreAnalyzeScript，提取命令并检测威胁，
            // 然后通过 AuthorizationRequested 事件向宿主请求授权决策（非阻塞）。
            if (AuthorizationRequested != null)
            {
                // 有事件订阅者 → 执行预授权（将决策缓存，ShouldRun 只查缓存不阻塞）
                if (!await PreAuthorizeScriptAsync(script))
                    return Localized.ScriptEngine_Auth_UserRejectedOperation;
            }
            else
            {
                // 无事件订阅者 → 仅做预分析（命令路径/URL 提取），
                // 授权由 ShouldRun 中的 Handler 处理（可能在后台线程阻塞，不会死锁）
                PreAnalyzeScript(script);
            }

            try
            {
                using var ps = PowerShell.Create(_runspace);
                ps.AddScript(script).AddCommand("Out-String").AddParameter("Width", 4096);
                // 在后台线程上执行，确保 ShouldRun() 授权回调不会阻塞 UI 线程。
                // 需要 UI 线程的 cmdlet（写操作）会通过 DraftPageCmdletBase 自动调度到 UI 线程。
                var results = await Task.Run(() => ps.InvokeAsync());

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
                // 清除预授权缓存（当次脚本执行结束）
                AuthorizationManager?.ClearPreAuthCache();
                _authWarning = null;
                _authThreatLevel = null;
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

                if (PSCommandAuthorizationHelper.AuditMode && analysis.IsSuspicious)
                {
                    Logger.Log($"[CommandFilter] 脚本威胁级别: {analysis.ThreatLevel}, " +
                               $"标记: {string.Join(", ", analysis.Flags)}, " +
                               $"混淆模式: {analysis.Obfuscations.Count} 个");
                }

                if (analysis.ThreatLevel >= ThreatLevel.Critical)
                {
                    throw new NotAllowedCommandException(NotAllowedCommandException.DeniedReason.DisallowedByInternalRules, Localized.ScriptEngine_NotAllowedBecauseHighThreatLevel(analysis.Summary));
                }
                else if (analysis.ThreatLevel >= ThreatLevel.Medium)
                {
                    // 不在分析阶段调用 Handler（避免阻塞），存储信息供 PreAuthorizeScriptAsync 使用
                    _authWarning = analysis.Summary;
                    _authThreatLevel = analysis.ThreatLevel;
                }



                // 2. 提取命令参数（路径/URL），供授权管理器使用
                var cmdParams = CommandFilter.AnalyzeCommands(script);
                PendingCommandParameters.Value = cmdParams;
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "[CommandFilter] analyze the script");
                throw;

            }
        }

        /// <summary>
        /// 预授权所有需要用户确认的命令（在管道执行之前调用）。
        /// 通过 <see cref="AuthorizationRequested"/> 事件向宿主请求决策，
        /// 宿主的异步事件处理程序通过 <see cref="AuthorizationRequestedEventArgs.Completion"/>
        /// TCS 返回结果。此方法将决策缓存到 <see cref="PSCommandAuthorizationHelper"/> 的预授权缓存中。
        /// </summary>
        /// <returns>true 表示继续执行；false 表示用户取消了执行。</returns>
        public async Task<bool> PreAuthorizeScriptAsync(string script)
        {
            _authWarning = null;
            _authThreatLevel = null;

            // 先运行预分析（提取命令、检测威胁）
            PreAnalyzeScript(script);

            var pendingParams = PendingCommandParameters.Value;
            var authContexts = new List<AuthorizationContext>();

            // 构建需要授权的命令上下文列表
            if (pendingParams != null)
            {
                foreach (var param in pendingParams)
                {
                    if (string.IsNullOrWhiteSpace(param.CommandName)) continue;

                    // 完成路径安全检查
                    if (param.PathSafetyStatus == PathSafety.Unresolved
                        && !string.IsNullOrEmpty(param.TargetPath)
                        && !string.IsNullOrEmpty(CommandFilter.WorkingPath))
                    {
                        param.PathSafetyStatus = CommandFilter.CheckPathSafety(param.TargetPath);
                    }

                    if (PSCommandAuthorizationHelper.RequiresUserAuthorization(param.CommandName, param))
                    {
                        authContexts.Add(new AuthorizationContext
                        {
                            CommandInfo = null,
                            CommandOrigin = CommandOrigin.Internal,
                            TargetPath = param.TargetPath,
                            PathSafetyStatus = param.PathSafetyStatus,
                            TargetUrl = param.TargetUrl,
                        });
                    }
                }
            }

            // 没有需要授权的命令且没有威胁警告 → 直接放行
            if (authContexts.Count == 0 && _authWarning == null)
                return true;

            // 没有订阅者 → 跳过预授权（管道执行时会通过 Handler 处理）
            if (AuthorizationRequested == null)
                return true;

            // 构建命令名列表供事件订阅者参考
            var commandNames = new Dictionary<string, AuthorizationContext>(StringComparer.OrdinalIgnoreCase);
            foreach (var ctx in authContexts)
            {
                var cmdName = ctx.CommandInfo?.Name ?? "";
                if (string.IsNullOrEmpty(cmdName))
                    cmdName = $"{Localized._Unknown} ({Localized.ScriptEngine_Auth_TargetPathLabel}{ctx.TargetPath ?? "—"})";
                if (!commandNames.ContainsKey(cmdName))
                    commandNames[cmdName] = ctx;
            }

            // 发起授权请求事件（非阻塞）
            var tcs = new TaskCompletionSource<Dictionary<string, AuthorizationResult>>();
            var cmdNames = authContexts.Select(ctx =>
            {
                // 从 PendingCommandParameters 中查找命令名
                var param = pendingParams?.FirstOrDefault(p =>
                    p.TargetPath == ctx.TargetPath && p.TargetUrl == ctx.TargetUrl);
                return param?.CommandName ?? Localized._Unknown;
            }).ToList();

            AuthorizationRequested?.Invoke(this,
                new AuthorizationRequestedEventArgs(authContexts, cmdNames, _authWarning, _authThreatLevel, tcs));

            // 等待宿主决策
            var decisions = await tcs.Task;

            // 用户取消了执行
            if (decisions == null || decisions.Count == 0)
                return false;

            // 缓存决策到预授权缓存
            if (AuthorizationManager != null)
            {
                foreach (var (cmdName, result) in decisions)
                {
                    if (result is AuthorizationResult.Allow or AuthorizationResult.AllowAndRemember
                        or AuthorizationResult.Deny or AuthorizationResult.DenyAndRemember)
                    {
                        AuthorizationManager.SetPreAuthCache(cmdName, result);
                    }
                }
            }

            return true;
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

        public void Reset()
        {
            _runspace?.Close();
            _runspace?.Dispose();
            _runspace = null;
            Initialize(CurrentPage, AuthorizationManager?.AuthorizationHandler, AuthorizationManager?.EnhancedAuthorizationHandler);
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

            // EffectProvider Management
            new SessionStateCmdletEntry("Get-EffectProviderTypes", typeof(GetProjectEffectProviderTypeCommand), null),
            new SessionStateCmdletEntry("Get-EffectProviderField", typeof(GetEffectProviderFieldCommand), null),
            new SessionStateCmdletEntry("Get-ProjectClipEffectProvider", typeof(GetProjectClipEffectProviderCommand), null),
            new SessionStateCmdletEntry("Add-ProjectClipEffectProvider", typeof(AddProjectClipEffectProviderCommand), null),
            new SessionStateCmdletEntry("Set-ProjectClipEffectProvider", typeof(SetProjectClipEffectProviderCommand), null),
            new SessionStateCmdletEntry("Remove-ProjectClipEffectProvider", typeof(RemoveProjectClipEffectProviderCommand), null),

            // Text Management
            new SessionStateCmdletEntry("Get-TextStyleField", typeof(GetTextStyleFieldCommand), null),
            new SessionStateCmdletEntry("Add-ProjectTextClip", typeof(AddProjectTextClipCommand), null),
            new SessionStateCmdletEntry("Set-ProjectTextClipStyle", typeof(SetProjectTextClipStyleCommand), null),

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

            // AI generated content
            new SessionStateCmdletEntry("New-AIGeneratedImage", typeof(NewAIGeneratedImageCommand), null),
            new SessionStateCmdletEntry("New-AIGeneratedVideo", typeof(NewAIGeneratedVideoCommand), null),
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
