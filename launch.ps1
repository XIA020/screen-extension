[CmdletBinding()]
param([switch] $Check, [switch] $Desktop, [switch] $Invite, [switch] $PrepareDisplay, [switch] $ReleaseDisplay)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$displayWorker = 'D:\APP\chuanliu\EasyVirtualDisplay\VirtualDisplayProject.exe'
$sunshineConfig = 'D:\APP\chuanliu\Sunshine2\config\sunshine.conf'
$managedPath = Join-Path $PSScriptRoot 'managed.json'
$managed = if (Test-Path -LiteralPath $managedPath) { Get-Content -LiteralPath $managedPath -Raw | ConvertFrom-Json }
if ($managed) { $sunshineConfig = $managed.sunshineConfig }

function Wait-Ready {
    param([scriptblock] $Probe, [int] $Seconds, [string] $Failure)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $Probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 350
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}

function Get-HotspotAddress {
    foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($adapter.OperationalStatus -ne 'Up' -or $adapter.Description -notlike '*Wi-Fi Direct Virtual Adapter*') {
            continue
        }
        foreach ($entry in $adapter.GetIPProperties().UnicastAddresses) {
            if ($entry.Address.AddressFamily -eq 'InterNetwork' -and $entry.Address.ToString() -notlike '169.254.*') {
                return $entry.Address.ToString()
            }
        }
    }
}

function Start-MobileHotspot {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $network = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]
    $managerType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
    $profile = $network::GetInternetConnectionProfile()
    if (-not $profile) {
        $existingAddress = Get-HotspotAddress
        if ($existingAddress) { return $existingAddress }
        throw 'No internet connection profile（找不到可共享的网络。请先接上网线或连接 Wi-Fi）'
    }

    $manager = $managerType::CreateFromConnectionProfile($profile)
    if ($manager.TetheringOperationalState -eq 'InTransition') {
        $null = Wait-Ready { $manager.TetheringOperationalState -ne 'InTransition' } 20 'Hotspot transition timed out（移动热点切换超时）'
    }
    if ($manager.TetheringOperationalState -ne 'On') {
        if ($Check) { throw 'Hotspot is off（移动热点目前关闭）' }

        $operation = $manager.StartTetheringAsync()
        $resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]
        $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
            $_.GetGenericArguments().Length -eq 1 -and $_.GetParameters().Length -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name.StartsWith('IAsyncOperation')
        } | Select-Object -First 1
        $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
        if (-not $task.Wait(20000)) { throw 'StartTetheringAsync timed out（开启移动热点超时）' }
        $result = $task.Result
        if ($result.Status -ne 'Success' -and $manager.TetheringOperationalState -ne 'On') {
            throw "$($result.Status)（Windows 未能开启移动热点） $($result.AdditionalErrorMessage)"
        }
    }
    Wait-Ready { Get-HotspotAddress } 12 'No hotspot IPv4 address（热点已开启，但网卡没有可用的 IPv4 地址）'
}

function Get-SunshineState {
    $request = [Net.HttpWebRequest]::Create("http://127.0.0.1:$script:sunshinePort/serverinfo")
    $request.Proxy = $null
    $request.Timeout = 1000
    $request.ReadWriteTimeout = 1000
    try {
        $response = $request.GetResponse()
        try {
            $reader = New-Object IO.StreamReader($response.GetResponseStream())
            try {
                $reply = [xml] $reader.ReadToEnd()
                $state = $reply.SelectSingleNode('/root/state')
                if ($state -and $state.InnerText -in 'SUNSHINE_SERVER_FREE', 'SUNSHINE_SERVER_BUSY') {
                    return $state.InnerText
                }
            } finally { $reader.Dispose() }
        } finally { $response.Dispose() }
    } catch [Net.WebException] {
        if ($_.Exception.Response) { $_.Exception.Response.Dispose() }
    }
}

function Start-SunshineService {
    $script:sunshinePort = 47989
    if (Test-Path -LiteralPath $sunshineConfig -PathType Leaf) {
        $portSetting = Select-String -LiteralPath $sunshineConfig -Pattern '^\s*port\s*=\s*(\d+)\s*(?:#.*)?$' | Select-Object -Last 1
        if ($portSetting) { $script:sunshinePort = [int] $portSetting.Matches[0].Groups[1].Value }
    }
    $service = Get-Service -Name SunshineService
    if ($service.Status -eq 'StopPending') {
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(10))
    }
    if ($service.Status -eq 'Stopped') {
        if ($Check) { throw 'Service stopped（Sunshine 服务目前关闭）' }
        Write-Host '      正在启动 Sunshine。如弹出管理员确认，请点「是」。'
        $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
        $starter = Start-Process -FilePath $scPath -ArgumentList 'start SunshineService' -Verb RunAs -WindowStyle Hidden -PassThru
        if (-not $starter.WaitForExit(15000)) {
            throw 'Service start timed out（等待 Sunshine 服务启动超时）'
        }
        $service.Refresh()
        if ($starter.ExitCode -ne 0 -and $service.Status -eq 'Stopped') {
            throw "sc.exe exit $($starter.ExitCode)（Windows 未能启动 Sunshine 服务）"
        }
    }
    $timeout = if ($Check) { 3 } else { 25 }
    $state = Get-SunshineState
    if ($state) { return $state }
    if (-not $Check) {
        Write-Host '      正在等待 Sunshine 初始化。本机冷启动通常约需 20 秒。'
    }
    Wait-Ready { Get-SunshineState } $timeout 'No response from Sunshine serverinfo（Sunshine 串流接口未就绪）'
}

function Get-VirtualDisplay {
    if ($managed) { return [TabletDisplay.MonitorProbe]::FindExtendedVirtualDisplay('MttVDD') }
    [TabletDisplay.MonitorProbe]::FindExtendedParsec()
}

function Start-VirtualDisplay {
    $display = Get-VirtualDisplay
    if ($display) { return $display }
    if ($managed) { throw 'Virtual display unavailable（未检测到已启用的扩展屏，请检查 Windows 显示设置）' }
    if ($Check) { throw 'No active extended Parsec display（没有检测到已启用的 Parsec 扩展屏）' }

    Add-Type -Path (Join-Path $PSScriptRoot 'DisplayPaths.cs')
    $display = [TabletDisplay.DisplayPaths]::EnableVirtual($false, $null, $null)
    if ($display) { return $display }

    if (-not (Test-Path -LiteralPath $displayWorker -PathType Leaf)) {
        throw "File not found（找不到 EasyVirtualDisplay 的虚拟屏幕程序） $displayWorker"
    }
    $session = (Get-Process -Id $PID).SessionId
    $worker = Get-Process -Name VirtualDisplayProject -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $session
    if (-not $worker) {
        $workerOptions = @{
            FilePath = $displayWorker
            WorkingDirectory = Split-Path -LiteralPath $displayWorker
            WindowStyle = 'Hidden'
            PassThru = $true
        }
        $worker = Start-Process @workerOptions
        if ($worker.WaitForExit(1200) -and -not (Get-VirtualDisplay)) {
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = New-Object Security.Principal.WindowsPrincipal($identity)
            if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                Write-Host '      普通权限启动失败，正在请求管理员权限'
                $worker = Start-Process @workerOptions -Verb RunAs
            }
        }
    }

    Wait-Ready {
        $display = Get-VirtualDisplay
        if ($display) { return $display }
        if (@($worker).Count -eq 1 -and $worker.HasExited) {
            throw "VirtualDisplayProject exit $($worker.ExitCode)（虚拟屏幕程序提前退出。可检查驱动，或用管理员方式运行启动器）"
        }
    } 12 'No active extended Parsec display（Windows 未检测到 Parsec 扩展屏，请检查显示模式是否为扩展）'
}

function Send-DisplayInvitation {
    param([string] $Address)

    $profile = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]::GetInternetConnectionProfile()
    if (-not $profile) { throw 'No network profile（找不到热点共享的网络）' }
    $manager = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]::CreateFromConnectionProfile($profile)
    $clients = Wait-Ready {
        foreach ($client in $manager.GetTetheringClients()) {
            foreach ($name in $client.HostNames) {
                if ($name.Type -eq 'Ipv4') { $name.CanonicalName }
            }
        }
    } 12 'No hotspot client（平板尚未连接电脑热点）'

    $web = New-Object Net.WebClient
    $web.Proxy = $null
    try {
        $info = [xml] $web.DownloadString("http://127.0.0.1:$script:sunshinePort/serverinfo")
    } finally {
        $web.Dispose()
    }
    $hostId = $info.SelectSingleNode('/root/uniqueid').InnerText
    $null = [guid]::Parse($hostId)
    $message = "TabletDisplay/1$([char]10)$hostId$([char]10)$([guid]::NewGuid())"
    $payload = [Text.Encoding]::UTF8.GetBytes($message)
    $endpoint = New-Object Net.IPEndPoint([Net.IPAddress]::Parse($Address), 0)
    $sender = New-Object Net.Sockets.UdpClient($endpoint)
    try {
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            foreach ($clientAddress in $clients) {
                $null = $sender.Send($payload, $payload.Length, $clientAddress, 47995)
            }
            if ($attempt -lt 2) { Start-Sleep -Milliseconds 400 }
        }
    } finally {
        $sender.Dispose()
    }
}

if ($ReleaseDisplay) {
    try {
        if ($managed) { exit 0 }
        $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveUdpListeners()
        if ($listeners | Where-Object { $_.Port -in 47998,47999,48000 }) { exit 0 }
        $session = (Get-Process -Id $PID).SessionId
        Get-Process -Name VirtualDisplayProject -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -eq $session -and $_.Path -eq $displayWorker } |
            ForEach-Object {
                Stop-Process -InputObject $_
                if (!$_.WaitForExit(3000)) { throw 'Virtual display worker did not exit（虚拟屏进程未退出）' }
            }
        exit 0
    } catch {
        Write-Output $_.Exception.Message
        exit 1
    }
}

if ($PrepareDisplay) {
    try {
        Add-Type -Path (Join-Path $PSScriptRoot 'MonitorProbe.cs')
        $null = Start-VirtualDisplay
        exit 0
    } catch {
        Write-Output $_.Exception.Message
        exit 1
    }
}

$mutex = New-Object System.Threading.Mutex($false, 'Local\TabletDisplayLauncher')
$ownsMutex = $false
$step = '初始化'
try {
    try {
        $ownsMutex = $mutex.WaitOne(0)
    } catch [Threading.AbandonedMutexException] {
        $ownsMutex = $true
    }
    if (-not $ownsMutex) {
        Write-Host '副屏启动器正在运行。'
        if ($Invite) { exit 1 }
        exit 0
    }

    $step = '移动热点'
    Write-Host '[1/3] 检查移动热点'
    $hotspotReady = $true
    try {
        $address = Start-MobileHotspot
        Write-Host "      已就绪，电脑地址 $address"
    } catch {
        if (!$managed -or $Check) { throw }
        $hotspotReady = $false
        Write-Warning "Windows 未能开启移动热点。仍可在本机打开控制面板。$($_.Exception.Message)"
        $address = '127.0.0.1'
    }

    $step = 'Sunshine'
    Write-Host '[2/3] 检查 Sunshine'
    $state = Start-SunshineService
    $streaming = $state -eq 'SUNSHINE_SERVER_BUSY'
    $description = if ($streaming) { '已有串流连接' } else { '已就绪' }
    Write-Host "      $description"

    $step = '虚拟屏幕'
    Write-Host '[3/3] 检查虚拟屏幕'
    Add-Type -Path (Join-Path $PSScriptRoot 'MonitorProbe.cs')
    if ($Invite -or $Check) {
        $display = Start-VirtualDisplay
        Write-Host "      已就绪，Windows 扩展屏 $display"
    } else {
        Write-Host '      连接扩展设备时自动启用。'
    }
    Write-Host ''
    if ($streaming) {
        Write-Host '电脑端已就绪，Sunshine 已有串流会话。'
    } elseif ($hotspotReady) {
        Write-Host "电脑端已就绪。请在手机或平板上打开「屏幕扩展」。手动添加主机时使用 $address。"
    }
    if (($Invite -or $Desktop) -and -not $Check) {
        $step = '启动控制面板'
        $preview = Join-Path $PSScriptRoot 'CameraPreview.exe'
        if ($managed -and $hotspotReady) {
            $fallback = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 47996 -State Listen -ErrorAction SilentlyContinue
            if ($fallback) {
                Get-Process -Id $fallback.OwningProcess -ErrorAction SilentlyContinue |
                    Where-Object Path -eq $preview | Stop-Process
            }
        }
        if ((Test-Path -LiteralPath $preview) -and -not (Get-Process -Name CameraPreview -ErrorAction SilentlyContinue)) {
            Start-Process -FilePath $preview -ArgumentList $address, '47996' -WindowStyle Hidden
        }
        if ($Desktop -and (Test-Path -LiteralPath $preview)) {
            $null = Wait-Ready {
                $request = Start-Process -FilePath $preview -ArgumentList '--open-devices' -WindowStyle Hidden -Wait -PassThru
                $request.ExitCode -eq 0
            } 5 'Control panel unavailable（无法打开控制面板）'
        }
    }
    if ($Invite -and -not $Check) {
        $step = '发送邀请'
        if ($hotspotReady) {
            Write-Host '正在向热点上的设备发送副屏邀请。'
            Send-DisplayInvitation -Address $address
            Write-Host '邀请已发出，请在平板测试版的主界面确认连接。'
        } else {
            throw 'Hotspot unavailable（请开启电脑移动热点后再发送邀请）'
        }
    }
    if ($Desktop) { Start-Sleep -Seconds 6 }
    exit 0
} catch {
    Write-Host ""
    Write-Host "[$step] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}


