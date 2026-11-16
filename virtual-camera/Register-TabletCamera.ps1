$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'Source\bin\Release\net8.0-windows'
$target = Join-Path $env:ProgramFiles 'TabletDisplayCamera'
New-Item -ItemType Directory -Force $target | Out-Null
foreach ($name in 'VCamNetSampleSource.dll','VCamNetSampleSource.deps.json','VCamNetSampleSource.runtimeconfig.json','VCamNetSampleSource.comhost.dll','DirectNCore.dll') {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target -Force
}
$component = Join-Path $target 'VCamNetSampleSource.comhost.dll'
$registration = Start-Process -FilePath "$env:WINDIR\System32\regsvr32.exe" -ArgumentList ('/s "' + $component + '"') -WindowStyle Hidden -Wait -PassThru
exit $registration.ExitCode
