#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$destinationPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Output exists（请选择新的安装包输出路径）。' }
$workspace = Join-Path ([IO.Path]::GetTempPath()) ('TabletDisplay-Build-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $workspace 'payload'
$null = New-Item -ItemType Directory -Path $workspace
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$iconArgument = '/win32icon:' + (Join-Path $root 'ScreenExtension.ico')
try {
    & (Join-Path $PSScriptRoot 'Stage-Dependencies.ps1') -Destination $payload
    $app = Join-Path $payload 'app'
    $null = New-Item -ItemType Directory -Path $app
    foreach ($name in 'launch.ps1','launch.cmd','MonitorProbe.cs','ControlPanel.html','Start-SecondDisplay.ps1','DisplayPaths.cs','ScreenArrangement.cs') {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $app
    }
    $installationFiles = 'Install.ps1','VirtualDisplayDevice.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
    Copy-Item -LiteralPath $installationFiles -Destination $payload
    $panelSources = 'CameraPreview.cs','ControlPanel.cs','ScreenArrangement.cs','MonitorProbe.cs','DisplayPaths.cs','NearbyDevices.cs' |
        ForEach-Object { Join-Path $root $_ }
    $panelArgs = '/nologo','/target:winexe','/platform:x64',$iconArgument,"/out:$app\CameraPreview.exe",
        '/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Security.dll','/r:System.Web.dll','/r:System.Web.Extensions.dll'
    & $compiler @panelArgs @panelSources
    if ($LASTEXITCODE) { throw 'Control panel build failed（控制面板编译失败）。' }

    $cameraProject = Join-Path $root 'virtual-camera\Host\TabletCameraHost.csproj'
    & dotnet build $cameraProject --configuration Release --nologo
    if ($LASTEXITCODE) { throw 'Virtual camera build failed（虚拟摄像头组件编译失败）。' }

    foreach ($component in 'Host','Source') {
        $source = Join-Path $root "virtual-camera\$component\bin\Release\net8.0-windows"
        $relative = if ($component -eq 'Host') { 'Host\bin\Release\net8.0-windows' } else { 'Source' }
        $target = Join-Path $app "virtual-camera\$relative"
        $null = New-Item -ItemType Directory -Path $target -Force
        Get-ChildItem -LiteralPath $source -File | Where-Object Extension -ne '.pdb' | Copy-Item -Destination $target
    }
    Copy-Item -LiteralPath (Join-Path $root 'virtual-camera\LICENSE') -Destination (Join-Path $app 'virtual-camera')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ThirdParty.md') -Destination $app
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $workspace 'payload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $built = Join-Path $workspace 'TabletDisplay-Setup.exe'
    $manifest = '/win32manifest:' + (Join-Path $PSScriptRoot 'setup.manifest')
    $setupArgs = '/nologo','/target:winexe','/platform:x64',$manifest,$iconArgument,"/out:$built","/resource:$zip,payload.zip",
        '/r:System.Windows.Forms.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll'
    & $compiler @setupArgs (Join-Path $PSScriptRoot 'Setup.cs')
    if ($LASTEXITCODE) { throw 'Installer build failed（安装包编译失败）。' }
    [IO.File]::Copy($built,$destinationPath,$false)
    Write-Output $destinationPath
} finally {
    $ownedDirectory = (Get-Item -LiteralPath $workspace).FullName
    $tempRoot = (Get-Item -LiteralPath ([IO.Path]::GetTempPath())).FullName.TrimEnd('\') + '\'
    if ($ownedDirectory.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($ownedDirectory).StartsWith('TabletDisplay-Build-')) {
        Remove-Item -LiteralPath $ownedDirectory -Recurse -Force
    }
}
