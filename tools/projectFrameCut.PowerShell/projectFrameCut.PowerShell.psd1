@{
    RootModule = 'projectFrameCut.PowerShell.dll'
    ModuleVersion = '1.1.0'
    GUID = '9901ed15-a0dc-433c-b5b7-8cc62a5c8fa4'
    Author = 'projectFrameCut'
    Description = 'Manage local projectFrameCut projects offline and edit authorized GUI projects through RPC.'
    PowerShellVersion = '7.6.0'
    CompatiblePSEditions = @('Core')
    RequiredAssemblies = @('protobuf-net.Core.dll', 'protobuf-net.dll', 'projectFrameCut.Render.Contracts.dll')
    CmdletsToExport = @(
        'Connect-ProjectFrameCut',
        'Connect-ProjectFrameCutPersistent',
        'Disconnect-ProjectFrameCut',
        'Get-Project',
        'New-Project',
        'Rename-Project',
        'Remove-Project',
        'Start-Project',
        'Get-ProjectInstance',
        'Get-ProjectTemplateVariable',
        'Get-ProjectInfo',
        'Save-Project',
        'Get-ProjectHistory',
        'Undo-ProjectHistory',
        'Redo-ProjectHistory',
        'Restore-ProjectHistory',
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
