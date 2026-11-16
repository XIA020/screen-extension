#Requires -Version 5.1
param([switch]$Stop, [switch]$IfIdle, [string]$FirstTargetBase64)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$profilePath = Join-Path $root 'managed.json'
$managed = Test-Path -LiteralPath $profilePath
$sunshine = if ($managed) {
    Join-Path $env:ProgramFiles 'TabletDisplaySunshine\sunshine.exe'
} else {
    'D:\APP\chuanliu\Sunshine2\sunshine.exe'
}
$basePort = 49989
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TabletDisplay\second-display'
$certificate = Join-Path $data 'certificate.pem'
$stateFile = Join-Path $data 'state.json'
$configFile = Join-Path $data 'sunshine.conf'
$secretFile = Join-Path $data 'pairing-secret'
$streamLog = Join-Path $data 'sunshine.log'
$apps = Join-Path $data 'apps.json'
$process = $null
$activated = $null
$runtime = Join-Path $data 'runtime.json'
$mutex = [Threading.Mutex]::new($false, 'Local\TabletDisplaySecondDisplay')
$locked = $false

function Read-Server {
    $request = [Net.HttpWebRequest]::Create("http://127.0.0.1:$basePort/serverinfo")
    $request.Proxy = $null
    $request.Timeout = 600
    $request.ReadWriteTimeout = 600
    try {
        $reply = $request.GetResponse()
        try {
            $reader = [IO.StreamReader]::new($reply.GetResponseStream())
            try { return [xml]$reader.ReadToEnd() }
            finally { $reader.Dispose() }
        } finally { $reply.Dispose() }
    } catch [Net.WebException] {
        if ($_.Exception.Response) { $_.Exception.Response.Dispose() }
    }
}

function Read-CaptureId($log, $display) {
    if (!(Test-Path -LiteralPath $log)) { return }
    $contents = Get-Content -LiteralPath $log -Raw
    if (!$contents) { return }
    $escapedDisplay = [regex]::Escape((ConvertTo-Json -InputObject $display -Compress))
    $pattern = '"device_id"\s*:\s*"(?<id>\{[0-9a-fA-F-]{36}\})"\s*,\s*"display_name"\s*:\s*' + $escapedDisplay
    $matches = [regex]::Matches($contents, $pattern)
    if ($matches.Count) { return ([guid]$matches[$matches.Count - 1].Groups['id'].Value).ToString('B') }
}

function Write-Profile([string]$binding, [string]$displayId) {
    $configuration = @(
        "port = $basePort"
        'sunshine_name = TabletDisplay-2'
        "bind_address = $binding"
        'upnp = disabled'
        'system_tray = disabled'
        'gamepad_driver = none'
        'locale = zh'
        'origin_web_ui_allowed = pc'
        "log_path = $streamLog"
        "file_state = $stateFile"
        "cert = $certificate"
        "pkey = $(Join-Path $data 'certificate-key.pem')"
        "file_apps = $apps"
    )
    if ($displayId) { $configuration += "output_name = $displayId" }
    [IO.File]::WriteAllLines($configFile,$configuration,[Text.UTF8Encoding]::new($false))
}

function Start-Streamer {
    Start-Process -FilePath $sunshine -WorkingDirectory (Split-Path -LiteralPath $sunshine) `
        -ArgumentList ('"' + $configFile + '"') -WindowStyle Hidden -PassThru
}

function Set-SecondAdapter([bool]$Enable) {
    $verb = if ($Enable) { 'Enable' } else { 'Disable' }
    $task = Get-ScheduledTask -TaskName "TabletDisplay-SecondDisplay-$verb" -ErrorAction SilentlyContinue
    if (!$task) { return $false }
    if (([TabletDisplay.DisplayPaths]::AvailableMttCount() -gt 0) -eq $Enable) { return $true }
    Start-ScheduledTask -InputObject $task
    for ($attempt=0; $attempt -lt 28; $attempt++) {
        if (([TabletDisplay.DisplayPaths]::AvailableMttCount() -gt 0) -eq $Enable) { return $true }
        Start-Sleep -Milliseconds 250
    }
    throw "Second virtual adapter did not $($verb.ToLowerInvariant())（第二路虚拟显卡切换失败）"
}

$enabledAdapter = $false
try {
    try { $locked = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Second display startup is in progress（第二副屏正在启动）' }
    if (!(Test-Path -LiteralPath $sunshine -PathType Leaf)) { throw 'Streaming component unavailable（找不到串流组件）' }
    Add-Type -AssemblyName System.Windows.Forms,System.Security
    $displaySources = 'MonitorProbe.cs','DisplayPaths.cs','ScreenArrangement.cs' | ForEach-Object { Join-Path $root $_ }
    Add-Type -Path $displaySources -ReferencedAssemblies 'System.Drawing','System.Windows.Forms','System.Core'
    $previousLayout = [TabletDisplay.DisplayPaths]::ReadDesktopLayout()
    $saved = if (Test-Path -LiteralPath $runtime) { Get-Content -LiteralPath $runtime -Raw | ConvertFrom-Json }
    $running = if ($saved) { Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$saved.processId)" }
    $ours = $running -and $running.ExecutablePath -eq $sunshine -and $running.CommandLine.Contains($configFile)
    $displays = @([TabletDisplay.MonitorProbe]::ExtendedVirtualDisplays('MttVDD'))
    $firstTarget = if ($FirstTargetBase64) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($FirstTargetBase64)) }
    $first = if ($managed) {
        $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
        if (!$firstTarget) { $firstTarget = $profile.primaryTarget }
        $mainLog = Join-Path (Split-Path -LiteralPath $profile.sunshineConfig) 'sunshine.log'
        $displays | Where-Object {
            if ($firstTarget) { [TabletDisplay.DisplayPaths]::VirtualTarget($_, $true) -eq $firstTarget }
            else { $profile.primaryDisplayId -and (Read-CaptureId $mainLog $_) -eq $profile.primaryDisplayId }
        } |
            Select-Object -First 1
    } else {
        [TabletDisplay.MonitorProbe]::FindExtendedParsec()
    }
    if ($first -and $managed -and !$firstTarget) {
        $firstTarget = [TabletDisplay.DisplayPaths]::VirtualTarget($first, $true)
    }
    if ($Stop) {
        if ($IfIdle) {
            $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners()
            if ($listeners | Where-Object { $_.Port -in 49998,49999,50000 }) { exit 0 }
        }
        if (!$saved) {
            if (!$managed) { $null = Set-SecondAdapter $false }
            $null = [ScreenArrangement]::PreservePositions($previousLayout, $null)
            exit 0
        }
        if ($ours) {
            $owned = Get-Process -Id $running.ProcessId -ErrorAction SilentlyContinue
            if ($owned) {
                Stop-Process -InputObject $owned
                $owned.WaitForExit()
            }
        }
        $attached = $displays | Where-Object {
            if ($saved.target) { [TabletDisplay.DisplayPaths]::VirtualTarget($_, $true) -eq $saved.target }
            else { $_ -eq $saved.display -and (Read-CaptureId $streamLog $_) -eq $saved.displayId }
        } | Select-Object -First 1
        $removed = !$managed -and (Set-SecondAdapter $false)
        if (!$removed -and $attached -and $attached -ne $first) {
            if (![TabletDisplay.DisplayPaths]::DisableMtt($attached)) {
                throw 'Second display did not detach（第二副屏未能停用）'
            }
        }
        $null = [ScreenArrangement]::PreservePositions($previousLayout, $null)
        Remove-Item -LiteralPath $runtime -Force
        exit 0
    }
    if ($managed -and !$firstTarget) { throw 'Primary virtual target unknown（无法识别第一路虚拟屏）' }
    $second = $displays | Where-Object { $_ -ne $first } | Select-Object -First 1
    if (!$managed -and [TabletDisplay.DisplayPaths]::AvailableMttCount() -eq 0) {
        $enabledAdapter = Set-SecondAdapter $true
        if ($enabledAdapter) { $second = [TabletDisplay.MonitorProbe]::FindExtendedVirtualDisplay('MttVDD') }
    }
    if (!$second) {
        $second = [TabletDisplay.DisplayPaths]::EnableVirtual($true, $null, $firstTarget)
        $activated = $second
    }
    if (!$second) { throw 'Second display unavailable（无法启用第二路虚拟屏，请检查驱动）' }
    $desktopMode = [ScreenArrangement]::Read($second)
    if ($desktopMode.Width -eq 800 -and $desktopMode.Height -eq 600) {
        $null = [ScreenArrangement]::SetResolution($second, 1920, 1080, $null)
    }
    if (![ScreenArrangement]::PreservePositions($previousLayout, $second)) {
        throw 'Screen arrangement could not be preserved（无法保留原有屏幕排列）'
    }
    $target = [Windows.Forms.Screen]::AllScreens | Where-Object DeviceName -EQ $second
    $primary = [Windows.Forms.Screen]::PrimaryScreen
    $firstScreen = [Windows.Forms.Screen]::AllScreens | Where-Object DeviceName -EQ $first
    if (!$target -or $target.Primary -or $target.Bounds.IntersectsWith($primary.Bounds) -or
        ($firstScreen -and $target.Bounds.IntersectsWith($firstScreen.Bounds))) {
        throw 'Second display is not independent（第二副屏必须处于扩展模式）'
    }

    $null = New-Item -ItemType Directory -Path $data -Force
    if (Read-Server) {
        if (!$ours -or $saved.display -ne $second) {
            throw 'Second stream conflicts with another display（第二副屏正在使用其他配置，请先退出）'
        }
        exit 0
    }

    Set-Content -LiteralPath $apps -Encoding Ascii -Value '{"env":{},"apps":[{"name":"Desktop","image-path":"desktop.png"}]}'
    Write-Profile '127.0.0.1' ''
    if (!(Test-Path -LiteralPath $secretFile)) {
        $password = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
        $creds = Start-Process -FilePath $sunshine -WorkingDirectory (Split-Path -LiteralPath $sunshine) `
            -ArgumentList ('"{0}" --creds tabletdisplay {1}' -f $configFile,$password) -WindowStyle Hidden -PassThru -Wait
        if ($creds.ExitCode -ne 0 -or !(Test-Path -LiteralPath $stateFile)) { throw 'Cannot initialize second pairing（无法初始化第二副屏配对）' }
        $secured = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($password),$null,'CurrentUser')
        Set-Content -LiteralPath $secretFile -Encoding Ascii -Value ([Convert]::ToBase64String($secured))
    }
    $process = Start-Streamer
    $displayId = $null
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'Screen probe stopped（第二副屏探测程序提前退出）' }
        if (Read-Server) {
            $displayId = Read-CaptureId $streamLog $second
            if ($displayId) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (!$displayId) { throw 'Capture ID unavailable（串流组件没有识别到第二副屏）' }
    Stop-Process -InputObject $process
    $process.WaitForExit()
    $process = $null

    Write-Profile '0.0.0.0' $displayId
    $process = Start-Streamer
    $offset = "(?m)^\s*Offset\s*:\s*$($target.Bounds.X)x$($target.Bounds.Y)\s*$"
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'Second streamer stopped（第二副屏串流启动失败）' }
        if ((Read-Server) -and (Test-Path -LiteralPath $certificate) -and
            (Read-CaptureId $streamLog $second) -eq $displayId -and (Select-String -LiteralPath $streamLog -Pattern $offset -Quiet)) {
            @{ processId = $process.Id; display = $second; displayId = $displayId
                target = [TabletDisplay.DisplayPaths]::VirtualTarget($second, $true) } | ConvertTo-Json -Compress |
                Set-Content -LiteralPath $runtime -Encoding UTF8
            $process = $null
            $activated = $null
            $enabledAdapter = $false
            exit 0
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'Capture verification failed（无法核验第二副屏的捕获位置）'
} catch {
    Write-Output $_.Exception.Message
    exit 1
} finally {
    if ($process -and !$process.HasExited) { Stop-Process -InputObject $process -ErrorAction SilentlyContinue }
    if ($activated) {
        try { $null = [TabletDisplay.DisplayPaths]::DisableMtt($activated) } catch {}
    }
    if ($enabledAdapter) {
        try { $null = Set-SecondAdapter $false } catch {}
    }
    if (($activated -or $enabledAdapter) -and $previousLayout) {
        try { $null = [ScreenArrangement]::PreservePositions($previousLayout, $null) } catch {}
    }
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
