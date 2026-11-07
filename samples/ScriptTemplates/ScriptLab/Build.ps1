$ErrorActionPreference = 'Stop'
$templatePath = Join-Path $PSScriptRoot 'template.json'
$template = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json -AsHashtable
$script = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'script.ps1'))
$template.ScriptHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($script))).ToLowerInvariant()
$templateJson = $template | ConvertTo-Json -Depth 30
[IO.File]::WriteAllText($templatePath, $templateJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$metadata = @{
    SourceTemplateID = $template.TemplateID; TemplateName = $template.TemplateName; Scope = $template.Scope
    Subtitle = '隔离 PowerShell / 多轨道 / 矢量 / 关键帧 / 参数与 Batch 测试'
    CreatedAt = '2026-11-07T00:00:00+08:00'; Tags = @('脚本', '示例', '动态图文', '测试')
    Readme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'README.md'))
} | ConvertTo-Json -Depth 10
$output = Join-Path $PSScriptRoot 'ScriptLab.pjfcTemplate'
$file = [IO.File]::Open($output, [IO.FileMode]::Create)
try {
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($item in @(
            @{ Name = 'template.json'; Text = $templateJson }
            @{ Name = 'metadata.json'; Text = $metadata }
            @{ Name = 'script.ps1'; Text = $script }
        )) {
            $entry = $zip.CreateEntry($item.Name, [IO.Compression.CompressionLevel]::Optimal)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($item.Text) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $file.Dispose() }
Write-Output $output
