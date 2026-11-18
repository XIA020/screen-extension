#Requires -Version 5.1
#Requires -RunAsAdministrator
param([switch]$PrepareSecondDisplay)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$app = Join-Path $env:ProgramFiles 'TabletDisplay'
$sunshine = Join-Path $env:ProgramFiles 'TabletDisplaySunshine'
$config = Join-Path $sunshine 'config'
$driver = Join-Path $PSScriptRoot 'VirtualDisplayDriver'
$logs = Join-Path $env:ProgramData 'TabletDisplay'

function Invoke-Installer([string]$File, [string]$Arguments, [int[]]$Allowed = @(0)) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -notin $Allowed) {
        throw "Installer failed（安装命令失败） $([IO.Path]::GetFileName($File))，退出码 $($process.ExitCode)。"
    }
    return $process.ExitCode
}

function Find-DisplayId([string]$Log, [string]$Display) {
    if (!(Test-Path -LiteralPath $Log)) { return }
    $contents = Get-Content -LiteralPath $Log -Raw
    if (!$contents) { return }
    $name = [regex]::Escape((ConvertTo-Json -InputObject $Display -Compress))
    $matches = [regex]::Matches($contents, '"device_id"\s*:\s*"(?<id>\{[0-9a-fA-F-]{36}\})"\s*,\s*"display_name"\s*:\s*' + $name)
    if ($matches.Count) { return ([guid]$matches[$matches.Count - 1].Groups['id'].Value).ToString('B') }
}

if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or [Environment]::OSVersion.Version.Build -lt 22000) {
    throw 'Unsupported Windows version（测试安装包需要 Windows 11 x64）。'
}
$service = Get-CimInstance Win32_Service -Filter "Name='SunshineService'"
$existing = $service -and $service.PathName.Trim('"') -ne (Join-Path $sunshine 'tools\sunshinesvc.exe')
$localUpgrade = $existing -and
    $service.PathName.Trim('"') -eq 'D:\APP\chuanliu\Sunshine2\tools\sunshinesvc.exe' -and
    (Test-Path -LiteralPath 'D:\Projects\TabletDisplayLauncher\existing-sunshine.json')
if ($localUpgrade) { $PrepareSecondDisplay = $true }
if ($PrepareSecondDisplay -and !$existing) {
    throw 'No separate streaming service（此选项用于已经配置第一副屏的电脑）'
}
if (!$PrepareSecondDisplay -and ((Get-Service SunshineSvc -ErrorAction SilentlyContinue) -or
    $existing)) {
    throw 'Existing Sunshine found（检测到现有 Sunshine 服务。请在干净的 Windows 上试装）。'
}
if (!$service -and (Get-Process sunshine -ErrorAction SilentlyContinue)) {
    throw 'Existing Sunshine running（请先退出正在运行的 Sunshine，再重试）。'
}

if (!$PrepareSecondDisplay) { $null = New-Item -ItemType Directory -Path $app,$logs -Force }
Add-Type -Path (Join-Path $PSScriptRoot 'app\MonitorProbe.cs')
Add-Type -Path (Join-Path $PSScriptRoot 'VirtualDisplayDevice.cs')
Write-Host '正在安装虚拟显示驱动…'
$driverConfig = Join-Path $env:SystemDrive 'VirtualDisplayDriver\vdd_settings.xml'
if (!(Test-Path -LiteralPath $driverConfig)) {
    $null = New-Item -ItemType Directory -Path (Split-Path $driverConfig) -Force
    Copy-Item -LiteralPath (Join-Path $driver 'vdd_settings.xml') -Destination $driverConfig
}
$device = Get-PnpDevice -Class Display -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
    $ids = Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue
    $ids.Data -contains 'Root\MttVDD'
} | Select-Object -First 1
if (!$device -and [VirtualDisplayDevice]::Install((Join-Path $driver 'MttVDD.inf'))) {
    throw 'Restart required（驱动要求重启。重启后再次运行安装包即可继续）。'
}

function Enable-SecondDisplayPorts([string]$Executable) {
    $rules = @(
        @{Protocol = 'TCP'; Ports = '49984','49989','50010'},
        @{Protocol = 'UDP'; Ports = '49998','49999','50000','50002','50010'}
    )
    foreach ($rule in $rules) {
        $name = 'TabletDisplay-Second-' + $rule.Protocol
        $old = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
        if (!$old) {
            $null = New-NetFirewallRule -Name $name -DisplayName $name -Direction Inbound -Action Allow `
                -Program $Executable -Protocol $rule.Protocol -LocalPort $rule.Ports -RemoteAddress LocalSubnet
        }
    }
}

function Install-SecondDisplayTasks {
    $adapter = @(Get-PnpDevice -PresentOnly -Class Display | Where-Object { $_.InstanceId -like 'ROOT\MTTVDD\*' })
    if ($adapter.Count -ne 1) { return }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Highest
    $tool = Join-Path $env:SystemRoot 'System32\pnputil.exe'
    foreach ($verb in 'Enable','Disable') {
        $action = New-ScheduledTaskAction -Execute $tool -Argument ('/{0}-device "{1}"' -f $verb.ToLowerInvariant(), $adapter[0].InstanceId)
        $null = Register-ScheduledTask -TaskName "TabletDisplay-SecondDisplay-$verb" -Action $action -Principal $principal -Force
    }
}

function Add-WideDisplayMode([string]$ConfigPath) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($ConfigPath)
    $resolutions = $xml.SelectSingleNode('/vdd_settings/resolutions')
    if (!$resolutions -or $resolutions.SelectSingleNode('resolution[width="2200" and height="1000"]')) { return }
    $entry = $xml.CreateElement('resolution')
    foreach ($field in 'width','height','refresh_rate') {
        $node = $xml.CreateElement($field)
        $node.InnerText = @{width='2200';height='1000';refresh_rate='60'}[$field]
        $null = $entry.AppendChild($node)
    }
    $null = $resolutions.AppendChild($entry)
    $stage = $ConfigPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $xml.Save($stage)
        [IO.File]::Replace($stage,$ConfigPath,($ConfigPath + '.before-widescreen'))
    } finally {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Force }
    }
}

function Update-ExistingPanel([string]$Target, [string]$Source) {
    $files = 'CameraPreview.exe','ControlPanel.html','DisplayPaths.cs','Start-SecondDisplay.ps1','launch.ps1','ScreenArrangement.cs'
    $backup = Join-Path $Target ('panel-update-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $backup
    foreach ($name in $files) {
        if (!(Test-Path -LiteralPath (Join-Path $Target $name) -PathType Leaf)) {
            throw 'Existing panel file is missing（缺少待升级的控制面板文件）。'
        }
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination (Join-Path $backup "$name.new")
    }

    $exe = Join-Path $Target 'CameraPreview.exe'
    Get-CimInstance Win32_Process -Filter "Name='CameraPreview.exe'" |
        Where-Object ExecutablePath -eq $exe | ForEach-Object {
            $panel = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
            if ($panel -and $panel.Path -eq $exe) {
                Stop-Process -InputObject $panel
                if (!$panel.WaitForExit(5000)) {
                    throw 'The control panel did not exit（控制面板未退出，请退出后重新运行安装包）。'
                }
            }
        }
    $replaced = New-Object 'System.Collections.Generic.List[string]'
    try {
        foreach ($name in $files) {
            [IO.File]::Replace((Join-Path $backup "$name.new"), (Join-Path $Target $name),
                (Join-Path $backup "$name.old"))
            $replaced.Add($name)
        }
    } catch {
        foreach ($name in $replaced) {
            [IO.File]::Replace((Join-Path $backup "$name.old"), (Join-Path $Target $name),
                (Join-Path $backup "$name.failed"))
        }
        throw
    }
}

if ($PrepareSecondDisplay) {
    $existingRoot = Split-Path -Parent (Split-Path -Parent $service.PathName.Trim('"'))
    $existingExe = Join-Path $existingRoot 'sunshine.exe'
    if (!(Test-Path -LiteralPath $existingExe -PathType Leaf)) { throw 'Sunshine not found（未找到现有串流组件）' }
    Enable-SecondDisplayPorts $existingExe
    if ($localUpgrade) {
        Add-WideDisplayMode $driverConfig
        Install-SecondDisplayTasks
        Update-ExistingPanel 'D:\Projects\TabletDisplayLauncher' (Join-Path $PSScriptRoot 'app')
        exit 11
    }
    exit 10
}

if ($service -and !$existing -and
    (Test-Path -LiteralPath (Join-Path $app 'managed.json') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $app 'pairing-secret') -PathType Leaf)) {
    Update-ExistingPanel $app (Join-Path $PSScriptRoot 'app')
    exit 11
}

Add-Type -Path (Join-Path $PSScriptRoot 'app\DisplayPaths.cs')
if ([TabletDisplay.DisplayPaths]::AvailableMttCount() -lt 2) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.','MTTVirtualDisplayPipe',[IO.Pipes.PipeDirection]::Out)
    try {
        $pipe.Connect(8000)
        $command = [Text.Encoding]::Unicode.GetBytes('SETDISPLAYCOUNT 2' + [char]0)
        $pipe.Write($command,0,$command.Length)
        $pipe.Flush()
    } finally { $pipe.Dispose() }
    for ($attempt=0; $attempt -lt 20 -and [TabletDisplay.DisplayPaths]::AvailableMttCount() -lt 2; $attempt++) {
        Start-Sleep -Milliseconds 350
    }
    if ([TabletDisplay.DisplayPaths]::AvailableMttCount() -lt 2) {
        throw 'Second virtual target missing（虚拟显示驱动未提供第二块屏）'
    }
}

$display = $null
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    $display = [TabletDisplay.MonitorProbe]::FindExtendedVirtualDisplay('MttVDD')
    if ($display) { break }
    if ($attempt -eq 2) { $null = Invoke-Installer "$env:SystemRoot\System32\DisplaySwitch.exe" '/extend' }
    Start-Sleep -Milliseconds 500
}
if (!$display) { throw 'Virtual display unavailable（Windows 未识别到虚拟扩展屏）。' }

Write-Host '正在安装和配置串流组件…'
if (!$service) {
    $msi = Join-Path $PSScriptRoot 'Sunshine-Windows-AMD64-installer.msi'
    $msiLog = Join-Path $logs 'sunshine-install.log'
    $arguments = '/i "{0}" /qn /norestart INSTALL_ROOT="{1}" /L*v "{2}"' -f $msi,$sunshine,$msiLog
    $exitCode = Invoke-Installer "$env:SystemRoot\System32\msiexec.exe" $arguments @(0,3010)
    if ($exitCode -eq 3010) { throw 'Restart required（串流组件要求重启。重启后重新运行安装包）。' }
}
$sunshineExe = Join-Path $sunshine 'sunshine.exe'
if (!(Test-Path -LiteralPath $sunshineExe)) { throw 'Sunshine not found（未找到安装后的串流组件）。' }
Enable-SecondDisplayPorts $sunshineExe
$null = New-Item -ItemType Directory -Path $config -Force
$configFile = Join-Path $config 'sunshine.conf'
$settings = @('locale = zh','origin_web_ui_allowed = pc','upnp = disabled','system_tray = disabled','gamepad_driver = none')
if (Test-Path -LiteralPath $configFile) {
    $settings = @((Get-Content -LiteralPath $configFile) | Where-Object {
        $_ -notmatch '^\s*(locale|origin_web_ui_allowed|upnp|system_tray|gamepad_driver|output_name)\s*='
    }) + $settings
}
Stop-Service SunshineService
Set-Content -LiteralPath $configFile -Value $settings -Encoding Ascii
Set-Content -LiteralPath (Join-Path $config 'apps.json') -Encoding Ascii -Value '{"env":{},"apps":[{"name":"Desktop","image-path":"desktop.png"}]}'

$pairingSecret = Join-Path $app 'pairing-secret'
$password = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$null = Invoke-Installer $sunshineExe "--creds tabletdisplay $password"
$bytes = [Text.Encoding]::UTF8.GetBytes($password)
$encrypted = [Security.Cryptography.ProtectedData]::Protect($bytes,$null,'CurrentUser')
Set-Content -LiteralPath $pairingSecret -Encoding Ascii -Value ([Convert]::ToBase64String($encrypted))

$log = Join-Path $config 'sunshine.log'
if (Test-Path -LiteralPath $log) {
    Move-Item -LiteralPath $log -Destination (Join-Path $logs 'sunshine-before-setup.log') -Force
}
Start-Service SunshineService
$displayId = $null
try {
    for ($attempt = 0; $attempt -lt 35; $attempt++) {
        $displayId = Find-DisplayId $log $display
        if ($displayId) { break }
        Start-Sleep -Seconds 1
    }
} finally { Stop-Service SunshineService }
if (!$displayId) { throw 'Capture display not found（串流组件未识别到目标虚拟屏）。' }
Add-Content -LiteralPath $configFile -Value "output_name = $displayId" -Encoding Ascii

$firewall = @(
    @{ Name = 'TabletDisplay-Sunshine-TCP'; Program = $sunshineExe; Protocol = 'TCP'; Port = '47984','47989','48010' },
    @{ Name = 'TabletDisplay-Sunshine-UDP'; Program = $sunshineExe; Protocol = 'UDP'; Port = '47998','47999','48000','48002','48010' },
    @{ Name = 'TabletDisplay-Companion'; Program = (Join-Path $app 'CameraPreview.exe'); Protocol = 'TCP'; Port = '47996' }
)
foreach ($rule in $firewall) {
    Get-NetFirewallRule -Name $rule.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    $null = New-NetFirewallRule -Name $rule.Name -DisplayName $rule.Name -Direction Inbound -Action Allow -Program $rule.Program -Protocol $rule.Protocol -LocalPort $rule.Port -RemoteAddress LocalSubnet
}
$sunshineRules = Get-NetFirewallApplicationFilter -Program $sunshineExe -ErrorAction SilentlyContinue |
    Get-NetFirewallRule | Where-Object DisplayName -eq 'Sunshine'
foreach ($rule in $sunshineRules) { Remove-NetFirewallRule -Name $rule.Name }

Write-Host '正在安装控制面板…'
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'app') | Copy-Item -Destination $app -Recurse -Force
$desktopRuntime = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
if (!(Get-ChildItem -LiteralPath $desktopRuntime -Directory -Filter '8.0.*' -ErrorAction SilentlyContinue)) {
    $runtime = Join-Path $PSScriptRoot 'windowsdesktop-runtime-win-x64.exe'
    $result = Invoke-Installer $runtime '/install /quiet /norestart' @(0,3010)
    if ($result -eq 3010) { throw 'Restart required（摄像头组件要求重启。重启后重新运行安装包）。' }
}
$camera = Join-Path $app 'virtual-camera\Source\VCamNetSampleSource.comhost.dll'
$null = Invoke-Installer "$env:SystemRoot\System32\regsvr32.exe" ('/s "{0}"' -f $camera)
@{ sunshineConfig = $configFile; sunshineCertificate = (Join-Path $config 'credentials\cacert.pem'); primaryDisplayId = $displayId
    primaryTarget = [TabletDisplay.DisplayPaths]::VirtualTarget($display, $true) } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $app 'managed.json') -Encoding UTF8
$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$shortcut = Join-Path $desktop '屏幕扩展控制台.lnk'
if (!(Test-Path -LiteralPath $shortcut)) {
    $null = New-Item -ItemType Directory -Path $desktop -Force
    $temporaryLink = Join-Path $app ('TabletDisplay-' + [guid]::NewGuid().ToString('N') + '.lnk')
    try {
        $link = (New-Object -ComObject WScript.Shell).CreateShortcut($temporaryLink)
        $link.TargetPath = Join-Path $app 'launch.cmd'
        $link.WorkingDirectory = $app
        $link.IconLocation = (Join-Path $app 'CameraPreview.exe') + ',0'
        $link.Save()
        [IO.File]::Move($temporaryLink, $shortcut)
    } finally {
        if (Test-Path -LiteralPath $temporaryLink) {
            Remove-Item -LiteralPath $temporaryLink -Force
        }
    }
}
Start-Service SunshineService
$null = Invoke-Installer "$env:SystemRoot\System32\sc.exe" 'config SunshineService start= delayed-auto'
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $request = [Net.HttpWebRequest]::Create('http://127.0.0.1:47989/serverinfo')
    $request.Proxy = $null
    $request.Timeout = 800
    try {
        $response = $request.GetResponse()
        $response.Dispose()
        $ready = (Test-Path -LiteralPath (Join-Path $config 'credentials\cacert.pem')) -and
            (Find-DisplayId $log $display) -eq $displayId
        if ($ready) { break }
    } catch [Net.WebException] {
        if ($_.Exception.Response) { $_.Exception.Response.Dispose() }
    }
    Start-Sleep -Milliseconds 700
}
if (!$ready) { throw 'Sunshine startup failed（串流服务未准备就绪，请查看安装日志）。' }
Write-Host '安装完成。请打开桌面上的「屏幕扩展控制台」。'
