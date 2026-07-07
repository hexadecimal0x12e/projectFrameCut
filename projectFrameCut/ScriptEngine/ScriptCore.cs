using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
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
        /// 初始化脚本引擎，创建持久的 PowerShell 运行空间并注册内置命令。
        /// </summary>
        public void Initialize(DraftPage? page = null)
        {
            CurrentPage = page;

            // 创建与应用程序同进程的 PowerShell 运行空间，命令持久化
            _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault());
            _runspace.Open();

            // 将 DraftPage 作为全局变量暴露给 PowerShell 脚本
            _runspace.SessionStateProxy.SetVariable("page", page);

            // 注册内置的 PowerShell 函数到运行空间
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(GetBuiltInModuleScript());
            ps.Invoke();
        }

        /// <summary>
        /// 同步执行 PowerShell 脚本并返回格式化输出。
        /// 如果脚本会修改时间线，则必须在 UI 线程上调用。
        /// </summary>
        public string Execute(string script)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(script);
            ps.AddCommand("Out-String").AddParameter("Width", 4096);

            var results = ps.Invoke();
            var output = string.Concat(results.Select(r => r?.ToString() ?? ""));

            if (ps.HadErrors)
            {
                var errors = string.Join(Environment.NewLine,
                    ps.Streams.Error.Select(e => $"ERROR: {e}"));
                if (!string.IsNullOrEmpty(output))
                    output += Environment.NewLine + "---" + Environment.NewLine;
                output += errors;
            }

            return output.TrimEnd();
        }

        /// <summary>
        /// 异步执行 PowerShell 脚本并返回格式化输出。
        /// 如果脚本会修改时间线，则必须在 UI 线程上调用。
        /// </summary>
        public async Task<string> ExecuteAsync(string script)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(script);
            ps.AddCommand("Out-String").AddParameter("Width", 4096);

            var results = await ps.InvokeAsync();
            var output = string.Concat(results.Select(r => r?.ToString() ?? ""));

            if (ps.HadErrors)
            {
                var errors = string.Join(Environment.NewLine,
                    ps.Streams.Error.Select(e => $"ERROR: {e}"));
                if (!string.IsNullOrEmpty(output))
                    output += Environment.NewLine + "---" + Environment.NewLine;
                output += errors;
            }

            return output.TrimEnd();
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

        public void Dispose()
        {
            _runspace?.Dispose();
            _runspace = null;
        }

        /// <summary>
        /// 返回注册到 PowerShell 运行空间的内置函数脚本。
        /// </summary>
        private static string GetBuiltInModuleScript() => @"
<#
.SYNOPSIS
    获取当前 DraftPage 项目中所有或指定的 Clip。
.PARAMETER Id
    可选的 Guid，用于筛选特定 Clip。
.EXAMPLE
    Get-ProjectClip
.EXAMPLE
    Get-ProjectClip -Id 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx'
.EXAMPLE
    Get-ProjectClip | Where-Object Type -eq 'VideoClip'
#>
function Get-ProjectClip {
    param([Guid]$Id = [Guid]::Empty)

    $page = Get-Variable -Name 'page' -ValueOnly -Scope Global -ErrorAction SilentlyContinue
    if (-not $page) { Write-Error 'No DraftPage is loaded.'; return }

    $clips = $page.Clips.Values
    if ($Id -ne [Guid]::Empty) { $clips = $clips | Where-Object { $_.Id -eq $Id } }

    $clips | ForEach-Object {
        [PSCustomObject]@{
            Id       = $_.Id
            Name     = $_.DisplayName
            Type     = $_.ClipType.ToString()
            Track    = $_.origTrack
            StartX   = [Math]::Round($_.origX, 1)
            Length   = [Math]::Round($_.origLength, 1)
            Source   = $_.SourcePath
            Width    = $_.TargetWidth
            Height   = $_.TargetHeight
        }
    }
}

<#
.SYNOPSIS
    向当前 DraftPage 项目添加一个新的 Clip。
.PARAMETER Name
    Clip 的显示名称。
.PARAMETER Track
    放置 Clip 的轨道索引。
.PARAMETER StartX
    时间线上的起始 X 位置（像素）。
.PARAMETER Width
    Clip 在时间线上的宽度（像素）。
.PARAMETER FilePath
    可选的源文件路径（视频/图片/音频）。
.EXAMPLE
    Add-ProjectClip -Name '我的片段' -Track 0 -StartX 100 -Width 300
.EXAMPLE
    Add-ProjectClip -FilePath 'C:\video.mp4' -Track 0 -StartX 100
#>
function Add-ProjectClip {
    param(
        [string]$Name        = 'New Clip',
        [int]$Track          = 0,
        [double]$StartX      = 0,
        [double]$Width       = 300,
        [string]$FilePath    = ''
    )

    $page = Get-Variable -Name 'page' -ValueOnly -Scope Global -ErrorAction SilentlyContinue
    if (-not $page) { Write-Error 'No DraftPage is loaded.'; return }

    if ($FilePath -and -not (Test-Path $FilePath)) {
        Write-Error ""File '$FilePath' does not exist.""
        return
    }

    try {
        $clip = $page.CreateAndAddClip(
            [double]$StartX,
            [double]$Width,
            [int]$Track,
            $null,              # id
            [string]$Name,
            $null,              # background
            $null,              # prototype
            $true,              # resolveOverlap
            0,                  # relativeStart
            0,                  # maxFrames
            $null               # sourceElement
        )

        if ($FilePath) {
            $clip.SourcePath = [System.IO.Path]::GetFullPath($FilePath)
            $mode = [projectFrameCut.DraftStuff.ClipElementUI]::DetermineClipMode($FilePath)
            $clip.ClipType = $mode
            if ($mode -eq [projectFrameCut.Shared.ClipMode]::VideoClip -or
                $mode -eq [projectFrameCut.Shared.ClipMode]::AudioClip) {
                $clip.UpdateSourceDuration()
            }
        }

        [PSCustomObject]@{
            Id     = $clip.Id
            Name   = $clip.DisplayName
            Type   = $clip.ClipType.ToString()
            Track  = $clip.origTrack
            StartX = [Math]::Round($clip.origX, 1)
            Source = $clip.SourcePath
        }
    }
    catch {
        Write-Error ""Failed to add clip: $_""
    }
}
";
    }
}
