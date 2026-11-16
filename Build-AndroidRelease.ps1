#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination,
    [string]$SdkRoot = $env:ANDROID_HOME
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$client = Join-Path $PSScriptRoot 'tablet-client'
$destinationPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Destination exists（目标安装包已存在）' }
if (!$SdkRoot) { $SdkRoot = 'D:\Android' }
$buildTools = Join-Path $SdkRoot 'build-tools\34.0.0'
$signer = Join-Path $buildTools 'apksigner.bat'
$zipalign = Join-Path $buildTools 'zipalign.exe'
foreach ($file in $signer,$zipalign) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Tool not found（未找到安卓构建工具） $file" }
}
$keytool = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin\keytool.exe' }
else { (Get-Command keytool.exe -ErrorAction Stop).Source }

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Release build step failed（正式包构建步骤失败） $([IO.Path]::GetFileName($File)) [$LASTEXITCODE]"
    }
}

Push-Location -LiteralPath $client
try {
    Invoke-Checked (Join-Path $client 'gradlew.bat') @(':app:assembleNonRootRelease')
} finally { Pop-Location }

$unsigned = Join-Path $client 'app\build\outputs\apk\nonRoot\release\app-nonRoot-release-unsigned.apk'
$signingDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TabletDisplaySigning'
$keystore = Join-Path $signingDir 'release.p12'
$protectedPassword = Join-Path $signingDir 'release-password.dpapi'
$alias = 'tabletdisplay-release'
$scope = [Security.Cryptography.DataProtectionScope]::CurrentUser
$mutex = [Threading.Mutex]::new($false, 'Local\TabletDisplayReleaseSigning')
$locked = $false
$staged = Join-Path $signingDir ('release-' + [guid]::NewGuid().ToString('N') + '.apk')
try {
    try { $locked = $mutex.WaitOne(10000) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Signing already running（已有正式包签名任务正在运行）' }
    $null = New-Item -ItemType Directory -Path $signingDir -Force
    if (!(Test-Path -LiteralPath $protectedPassword)) {
        if (Test-Path -LiteralPath $keystore) { throw 'Signing password missing（已有发布密钥，但缺少其受保护口令）' }
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        $bytes = New-Object byte[] 32
        try {
            $random.GetBytes($bytes)
            $secret = [Text.Encoding]::UTF8.GetBytes([Convert]::ToBase64String($bytes))
            try {
                $protected = [Security.Cryptography.ProtectedData]::Protect($secret,$null,$scope)
                [IO.File]::WriteAllBytes($protectedPassword,$protected)
            } finally { [Array]::Clear($secret,0,$secret.Length) }
        } finally {
            $random.Dispose()
            [Array]::Clear($bytes,0,$bytes.Length)
        }
    }
    $clear = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($protectedPassword),$null,$scope)
    try { $env:TABLETDISPLAY_RELEASE_KEY_PASSWORD = [Text.Encoding]::UTF8.GetString($clear) }
    finally { [Array]::Clear($clear,0,$clear.Length) }
    if (!(Test-Path -LiteralPath $keystore)) {
        Invoke-Checked $keytool @('-genkeypair','-keystore',$keystore,'-storetype','PKCS12',
            '-storepass:env','TABLETDISPLAY_RELEASE_KEY_PASSWORD','-alias',$alias,'-keyalg','RSA',
            '-keysize','3072','-validity','10950','-dname','CN=Tablet Display Release','-noprompt')
    }
    Invoke-Checked $signer @('sign','--ks',$keystore,'--ks-key-alias',$alias,'--ks-pass',
        'env:TABLETDISPLAY_RELEASE_KEY_PASSWORD','--v4-signing-enabled','false','--out',$staged,$unsigned)
    Invoke-Checked $signer @('verify','--verbose','--print-certs',$staged)
    Invoke-Checked $zipalign @('-c','-p','4',$staged)
    [IO.File]::Copy($staged,$destinationPath,$false)
    Get-FileHash -LiteralPath $destinationPath
} finally {
    Remove-Item Env:TABLETDISPLAY_RELEASE_KEY_PASSWORD -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
