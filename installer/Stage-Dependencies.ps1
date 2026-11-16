#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination,
    [string]$Cache = (Join-Path ([IO.Path]::GetTempPath()) 'TabletDisplay-InstallerReview')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$assets = @(
    @{
        Name = 'Sunshine-Windows-AMD64-installer.msi'
        Release = 'https://github.com/LizardByte/Sunshine/releases/download/v2026.914.233613'
        Hash = '1D7FED8BEECD5889DC7FF14CF9F42D6D38F37C3066C13C6C2A5F4E91847E0CCF'
    },
    @{
        Name = 'VDD.Control.25.7.23.zip'
        Release = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23'
        Hash = 'A701F2272E9FCF382849B24F913C6DD07597B3B1116525F2E90182F019609154'
    },
    @{
        Name = 'windowsdesktop-runtime-win-x64.exe'
        Uri = 'https://dotnetcli.azureedge.net/dotnet/WindowsDesktop/8.0.34/windowsdesktop-runtime-8.0.34-win-x64.exe'
        Algorithm = 'SHA512'
        Hash = 'e5d9049668425fdf88029005b33b563cd4793d5afc6841c9b9e1622119a06466536a60cb09708e0b6d58457c1c0c49ac415decba47ec7a06ebdf828aff903f62'
    }
)

function Get-VerifiedAsset($Asset) {
    $path = Join-Path $Cache $Asset.Name
    $algorithm = if ($Asset.Algorithm) { $Asset.Algorithm } else { 'SHA256' }
    $uri = if ($Asset.Uri) { $Asset.Uri } else { "$($Asset.Release)/$($Asset.Name)" }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $path -Algorithm $algorithm).Hash -eq $Asset.Hash) {
            return $path
        }
    }
    $temporary = "$path.$([Guid]::NewGuid().ToString('N')).download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $temporary -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $temporary -Algorithm $algorithm).Hash -ne $Asset.Hash) {
            throw "Checksum mismatch（文件校验值不匹配）: $($Asset.Name)"
        }
        Move-Item -LiteralPath $temporary -Destination $path -Force
        return $path
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Confirm-Signature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') {
        throw "Invalid signature（签名验证失败）: $Path [$($signature.Status)]"
    }
}

$null = New-Item -ItemType Directory -Path $Cache -Force
$msi = Get-VerifiedAsset $assets[0]
$archive = Get-VerifiedAsset $assets[1]
$runtime = Get-VerifiedAsset $assets[2]
Confirm-Signature $msi
Confirm-Signature $runtime

$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if (Test-Path -LiteralPath $output) {
    throw "Destination already exists（请为本次构建指定新的输出目录）: $output"
}
$working = "$output.$([Guid]::NewGuid().ToString('N')).staging"
$driver = Join-Path $working 'VirtualDisplayDriver'
$null = New-Item -ItemType Directory -Path $driver -Force
try {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        # The upstream folder is called x86, but this INF declares NTamd64.
        foreach ($name in 'MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat', 'vdd_settings.xml') {
            $entry = $zip.GetEntry("SignedDrivers/x86/VDD/$name")
            if ($null -eq $entry) { throw "Missing driver file（缺少驱动文件）: $name" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $driver $name))
        }
    } finally {
        $zip.Dispose()
    }
    Confirm-Signature (Join-Path $driver 'mttvdd.cat')
    Copy-Item -LiteralPath $msi,$runtime -Destination $working
    [IO.Directory]::Move($working, $output)
    Write-Output "安装组件已准备：$output"
} finally {
    if (Test-Path -LiteralPath $working) {
        # Only clean up the staging directory created by this invocation.
        $ownedDirectory = (Get-Item -LiteralPath $working).FullName
        if ($ownedDirectory -eq [IO.Path]::GetFullPath($working) -and $ownedDirectory.EndsWith('.staging')) {
            Remove-Item -LiteralPath $ownedDirectory -Recurse -Force
        }
    }
}
