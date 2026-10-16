param(
    [string]$MSBuildPath = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    [string]$PlatformToolset = 'v143',
    [string[]]$MSBuildArguments = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
$deps = Join-Path $repoRoot 'artifacts/sandy-deps'
$destination = Join-Path $repoRoot 'projectFrameCut/Platforms/Windows/Sandy'
$revision = 'c14ac771708a003df84cdf1fb978235dcab40cc3'
New-Item -ItemType Directory -Path $deps, (Join-Path $destination 'win-x64'), (Join-Path $destination 'win-arm64') -Force | Out-Null

function Get-CheckedDownload([string]$Url, [string]$Path, [string]$Hash) {
    if (!(Test-Path -LiteralPath $Path)) { Invoke-WebRequest $Url -OutFile $Path }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Hash) {
        throw "SHA-256 mismatch: $Path"
    }
}

Get-CheckedDownload 'https://github.com/ahrvoje/sandy_cli/releases/download/v0.9996/sandy.exe' `
    (Join-Path $destination 'win-x64/sandy.exe') '40c1ad5b6236f7b0d3d76103d665f96cf992772b84ecfc7533aff33a4b442549'
Get-CheckedDownload "https://codeload.github.com/ahrvoje/sandy_cli/zip/$revision" `
    (Join-Path $deps 'sandy.zip') '7b5bd7f58dfa7b00479072e3660ac29e70ed113f2cca08c02dad69c84006205a'
Get-CheckedDownload 'https://codeload.github.com/ToruNiina/toml11/zip/refs/tags/v4.4.0' `
    (Join-Path $deps 'toml11.zip') '075dd130a9f517c3eaed502ece6a2c1088b1d0dd4aa79882fd0ca7609325ffdc'
Expand-Archive -LiteralPath (Join-Path $deps 'sandy.zip') -DestinationPath $deps -Force
Expand-Archive -LiteralPath (Join-Path $deps 'toml11.zip') -DestinationPath $deps -Force
$source = Join-Path $deps "sandy_cli-$revision"
New-Item -ItemType Directory -Path (Join-Path $deps 'toml11') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $deps 'toml11-4.4.0/include') -Destination (Join-Path $deps 'toml11') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $destination 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $deps 'toml11-4.4.0/LICENSE') -Destination (Join-Path $destination 'toml11-LICENSE.txt')

# Add native ARM64 configurations without changing Sandy's sandbox implementation.
foreach ($project in @('sandy.vcxproj', 'pipehook/sandy_pipe_hook.vcxproj', 'test/pipe_hook_app.vcxproj')) {
    $path = Join-Path $source $project
    [xml]$xml = Get-Content -LiteralPath $path -Raw
    $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('m', $xml.DocumentElement.NamespaceURI)
    foreach ($node in @($xml.SelectNodes('//m:ProjectConfiguration[contains(@Include,"|x64")]', $ns))) {
        $copy = $node.CloneNode($true)
        $copy.SetAttribute('Include', $copy.GetAttribute('Include').Replace('|x64', '|ARM64'))
        $copy.Platform = 'ARM64'
        $node.ParentNode.InsertAfter($copy, $node) | Out-Null
    }
    foreach ($node in @($xml.SelectNodes('//*[@Condition]', $ns))) {
        if ($node.GetAttribute('Condition').Contains('|x64')) {
            $copy = $node.CloneNode($true)
            $copy.SetAttribute('Condition', $copy.GetAttribute('Condition').Replace('|x64', '|ARM64'))
            $node.ParentNode.InsertAfter($copy, $node) | Out-Null
        }
    }
    foreach ($node in $xml.SelectNodes('//m:ItemDefinitionGroup/m:ClCompile', $ns)) {
        $options = $xml.CreateElement('AdditionalOptions', $xml.DocumentElement.NamespaceURI)
        $options.InnerText = '/utf-8 %(AdditionalOptions)'
        $node.AppendChild($options) | Out-Null
    }
    $xml.Save($path)
}
$solutionPath = Join-Path $source 'sandy.sln'
$solution = foreach ($line in Get-Content -LiteralPath $solutionPath) {
    $line
    if ($line.Contains('|x64')) { $line.Replace('|x64', '|ARM64') }
}
[IO.File]::WriteAllLines($solutionPath, [string[]]$solution)
if (!(Test-Path -LiteralPath $MSBuildPath)) { throw "MSBuild not found: $MSBuildPath. Install the C++ ARM64 build tools or pass -MSBuildPath." }

& $MSBuildPath $solutionPath /t:sandy /p:Configuration=Release /p:Platform=ARM64 "/p:PlatformToolset=$PlatformToolset" /v:minimal @MSBuildArguments
if ($LASTEXITCODE -ne 0) { throw "Sandy ARM64 build failed ($LASTEXITCODE)." }
$arm64Executable = Join-Path $source 'ARM64/Release/sandy.exe'
$bytes = [IO.File]::ReadAllBytes($arm64Executable)
if ([BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3c) + 4) -ne 0xaa64) {
    throw 'The built Sandy executable is not native ARM64.'
}
Copy-Item -LiteralPath $arm64Executable -Destination (Join-Path $destination 'win-arm64/sandy.exe') -Force
Get-FileHash -LiteralPath (Join-Path $destination 'win-x64/sandy.exe'), (Join-Path $destination 'win-arm64/sandy.exe') -Algorithm SHA256
