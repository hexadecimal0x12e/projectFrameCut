@{
    RootModule = 'projectFrameCut.PowerShell.dll'
    ModuleVersion = '1.0.0'
    GUID = '9901ed15-a0dc-433c-b5b7-8cc62a5c8fa4'
    Author = 'projectFrameCut'
    Description = 'Edit the authorized projectFrameCut GUI project through RPC.'
    PowerShellVersion = '7.6.5'
    CompatiblePSEditions = @('Core')
    RequiredAssemblies = @('protobuf-net.Core.dll', 'protobuf-net.dll', 'projectFrameCut.Render.Contracts.dll', 'projectFrameCut.Render.RPCProtocol.dll')
    CmdletsToExport = @(
        'Connect-ProjectFrameCut',
        'Disconnect-ProjectFrameCut',
        'Get-ProjectInfo',
        'Save-Project',
        'Get-ProjectClip',
        'Add-ProjectClip',
        'Set-ProjectClip',
        'Remove-ProjectClip',
        'Copy-ProjectClip',
        'Get-ProjectAsset',
        'Add-ProjectAsset',
        'Remove-ProjectAsset',
        'Get-ProjectTrack',
        'Add-ProjectTrack',
        'Get-ProjectTextStyle',
        'Get-ProjectTextStyleField',
        'Add-ProjectTextClip',
        'Set-ProjectTextClipStyle',
        'Get-ProjectEffectProviderType',
        'Get-ProjectEffectProviderField',
        'Get-ProjectClipEffectProvider',
        'Add-ProjectClipEffectProvider',
        'Set-ProjectClipEffectProvider',
        'Remove-ProjectClipEffectProvider'
    )
    FunctionsToExport = @()
    AliasesToExport = @()
    VariablesToExport = @()
}
