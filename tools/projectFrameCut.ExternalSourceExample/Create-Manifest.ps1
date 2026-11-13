param([Parameter(Mandatory)][string]$Directory)
$sourceRoot = (Resolve-Path -LiteralPath $Directory).Path
$manifestPath = Join-Path $sourceRoot 'external-source.json'
$files = [ordered]@{}
if (Get-ChildItem -LiteralPath $sourceRoot -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'Directory links are not supported in external sources.'
}
Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object FullName -ne $manifestPath | ForEach-Object {
    $files[[IO.Path]::GetRelativePath($sourceRoot, $_.FullName).Replace('\', '/')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[ordered]@{
    formatVersion = 1
    id = 'projectFrameCut.example.video'
    name = '示例外置视频源'
    version = '1.0.0'
    author = 'projectFrameCut'
    description = '8 位、16 位、Alpha 和 HDR 测试源'
    assembly = 'projectFrameCut.ExternalSourceExample.dll'
    entryPoint = 'projectFrameCut.ExternalSourceExample.ExampleProvider'
    files = $files
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
