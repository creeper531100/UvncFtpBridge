[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    # The uvnc.com download sits behind a form, so bundle the signed viewer from an existing UltraVNC install.
    [string]$VncViewerPath = (Join-Path $env:ProgramFiles 'uvnc bvba\UltraVNC\vncviewer.exe')
)
$ErrorActionPreference = 'Stop'
$rootPath = $PSScriptRoot
$cachePath = Join-Path $rootPath '.portable-cache'
$packageName = if ($FrameworkDependent) { 'UltraVNC-WinSCP-FileTunnel-Portable-FrameworkDependent' } else { 'UltraVNC-WinSCP-FileTunnel-Portable' }
$packagePath = Join-Path $rootPath "dist\$packageName"
$clientPath = Join-Path $packagePath 'WinSCP'
$version = '6.7'
$expectedHash = 'FEF88545873ACA4B353AC322B1E2702F2FEADF30758B713F5FC7E2BED60F7662'
New-Item -ItemType Directory -Path $cachePath,$clientPath -Force | Out-Null
$zipPath = Join-Path $cachePath "WinSCP-$version-Portable.zip"
if (-not (Test-Path -LiteralPath $zipPath)) {
    $page = Invoke-WebRequest -Uri "https://winscp.net/download/WinSCP-$version-Portable.zip/download"
    $url = $page.Links | Where-Object { $_.href -like "https://cdn.winscp.net/files/WinSCP-$version-Portable.zip*" } | Select-Object -First 1 -ExpandProperty href
    if (-not $url) { throw 'Official WinSCP portable download link was not found.' }
    Invoke-WebRequest -Uri $url -OutFile $zipPath
}
if ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'WinSCP portable SHA-256 does not match the official checksum.'
}
Expand-Archive -LiteralPath $zipPath -DestinationPath $clientPath -Force
$signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $clientPath 'WinSCP.exe')
if ($signature.Status -ne 'Valid') { throw 'Official WinSCP signature validation failed.' }

$translationZip = Join-Path $cachePath "WinSCP-$version-CHT.zip"
if (-not (Test-Path -LiteralPath $translationZip)) {
    Invoke-WebRequest -Uri "https://winscp.net/translations/dll/$version/ch.zip" -OutFile $translationZip
}
Expand-Archive -LiteralPath $translationZip -DestinationPath $clientPath -Force
$ftVersion = 'v3.5.5'
$ftHash = '25A5A7A248522B2F8FD310CCD7BC627A26F363D551C66162EF47DDD7BB9588C2'
$ftCache = Join-Path $cachePath "ft-$ftVersion-win-x64.exe"
if (-not (Test-Path -LiteralPath $ftCache)) {
    Invoke-WebRequest -Uri "https://github.com/fiddyschmitt/File-Tunnel/releases/download/$ftVersion/ft-win-x64.exe" -OutFile $ftCache
}
if ((Get-FileHash -LiteralPath $ftCache -Algorithm SHA256).Hash -ne $ftHash) {
    throw 'File-Tunnel SHA-256 does not match the pinned release checksum.'
}
Copy-Item -LiteralPath $ftCache -Destination (Join-Path $packagePath 'ft.exe') -Force
$sshVersion = '10.0.0.0p2-Preview'
$sshHash = '23F50F3458C4C5D0B12217C6A5DDFDE0137210A30FA870E98B29827F7B43ABA5'
$sshArchive = Join-Path $cachePath "OpenSSH-$sshVersion-Win64.zip"
if (-not (Test-Path -LiteralPath $sshArchive)) {
    Invoke-WebRequest -Uri "https://github.com/PowerShell/Win32-OpenSSH/releases/download/$sshVersion/OpenSSH-Win64.zip" -OutFile $sshArchive
}
if ((Get-FileHash -LiteralPath $sshArchive -Algorithm SHA256).Hash -ne $sshHash) { throw 'OpenSSH archive SHA-256 mismatch.' }
$sshExtract = Join-Path $cachePath "OpenSSH-$sshVersion"
Expand-Archive -LiteralPath $sshArchive -DestinationPath $sshExtract -Force
$sshPath = Join-Path $packagePath 'OpenSSH'
New-Item -ItemType Directory -Path $sshPath -Force | Out-Null
foreach ($name in @('ssh.exe','libcrypto.dll','LICENSE.txt','NOTICE.txt','sshd.exe','sshd-auth.exe','sshd-session.exe','ssh-shellhost.exe','ssh-keygen.exe','sftp-server.exe')) {
    $source = Join-Path $sshExtract "OpenSSH-Win64\$name"
    if ($name -match '\.(exe|dll)$') {
        $sig = Get-AuthenticodeSignature -LiteralPath $source
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft') { throw "Invalid OpenSSH signature: $name" }
    }
    Copy-Item -LiteralPath $source -Destination $sshPath -Force
}
$viewerVersion = '1.4.3.6'
if (-not (Test-Path -LiteralPath $VncViewerPath)) { throw "UltraVNC Viewer not found: $VncViewerPath (install UltraVNC $viewerVersion or pass -VncViewerPath)." }
$viewerSignature = Get-AuthenticodeSignature -LiteralPath $VncViewerPath
if ($viewerSignature.Status -ne 'Valid' -or $viewerSignature.SignerCertificate.Subject -notmatch '(^|, )CN=uvnc bvba(,|$)') {
    throw 'UltraVNC Viewer signature validation failed (expected a valid signature by uvnc bvba).'
}
if ((Get-Item -LiteralPath $VncViewerPath).VersionInfo.FileVersion -ne $viewerVersion) {
    throw "UltraVNC Viewer must be version $viewerVersion to match ThirdParty.txt and the tested Repeater settings."
}
$viewerLicense = Join-Path (Split-Path -Parent $VncViewerPath) 'Licence.rtf'
if (-not (Test-Path -LiteralPath $viewerLicense)) { throw "UltraVNC Licence.rtf not found beside the viewer: $viewerLicense" }
$viewerPath = Join-Path $packagePath 'UltraVNC'
New-Item -ItemType Directory -Path $viewerPath -Force | Out-Null
Copy-Item -LiteralPath $VncViewerPath,$viewerLicense -Destination $viewerPath -Force
# The viewer writes its settings beside itself; ship it without a previous run's options.vnc.
Remove-Item -LiteralPath (Join-Path $viewerPath 'options.vnc') -ErrorAction SilentlyContinue
& (Join-Path $rootPath 'Build-CLI.ps1') -FrameworkDependent:$FrameworkDependent
$cliFiles = @(if ($FrameworkDependent) {
    Get-ChildItem -LiteralPath (Join-Path $rootPath 'dist\FrameworkDependent') -File
} else {
    Get-Item -LiteralPath (Join-Path $rootPath 'dist\UvncFtpBridge.exe')
})
foreach ($cliFile in $cliFiles) { Copy-Item -LiteralPath $cliFile.FullName -Destination $packagePath }
Copy-Item -LiteralPath (Join-Path $rootPath 'Portable\WinSCP.ini') -Destination $clientPath
foreach ($item in @('Start-Direct.cmd','Start-FileTunnel.cmd','Start-FileTunnel-Remote.cmd','ft-options.txt','使用說明.txt','ThirdParty.txt','File-Tunnel-LICENSE.md')) {
    Copy-Item -LiteralPath (Join-Path $rootPath "Portable\$item") -Destination $packagePath
}
Copy-Item -LiteralPath (Join-Path $rootPath 'VNC-SSH-TUNNEL.zh-TW.md') -Destination $packagePath -Force
# Drop entry points from earlier builds: two launchers (direct / File-Tunnel) now open WinSCP and UltraVNC together,
# and the jump-host side runs as "UvncFtpBridge.exe --remote" instead of a PowerShell script.
foreach ($old in @('Start-WinSCP.vbs','Start-WinSCP.cmd','Start-WinSCP-FileTunnel.vbs','Start-WinSCP-FileTunnel.cmd','Start-VNC-FileTunnel.cmd','Start-FileTunnel-Remote.ps1')) {
    Remove-Item -LiteralPath (Join-Path $packagePath $old) -ErrorAction SilentlyContinue
}
if ($FrameworkDependent) {
    $instructionsPath = Join-Path $packagePath '使用說明.txt'
    $instructions = Get-Content -LiteralPath $instructionsPath -Raw -Encoding utf8
    $runtimeNote = "精簡版：使用前須安裝 .NET 9 Runtime x64（不需 SDK）。`r`n下載：https://dotnet.microsoft.com/en-us/download/dotnet/9.0`r`nEXE、DLL 與 JSON 檔案須保留在同一資料夾。`r`n`r`n"
    Set-Content -LiteralPath $instructionsPath -Value ($runtimeNote + $instructions) -Encoding utf8
}
$distributionZip = Join-Path $rootPath "dist\$packageName.zip"
# Only ship official client files, the CLI, the clean INI template, the signed viewer and the entry points.
$files = @(
    (Join-Path $packagePath 'Start-Direct.cmd'),
    (Join-Path $packagePath 'Start-FileTunnel.cmd'),
    (Join-Path $packagePath 'Start-FileTunnel-Remote.cmd'),
    (Join-Path $packagePath 'ft-options.txt'),
    (Join-Path $packagePath '使用說明.txt'),
    (Join-Path $packagePath 'ThirdParty.txt'),
    (Join-Path $packagePath 'File-Tunnel-LICENSE.md'),
    (Join-Path $packagePath 'VNC-SSH-TUNNEL.zh-TW.md'),
    (Join-Path $packagePath 'ft.exe'),
    $sshPath,
    $clientPath,
    $viewerPath
)
$files += @($cliFiles | ForEach-Object { Join-Path $packagePath $_.Name })
Compress-Archive -LiteralPath $files -DestinationPath $distributionZip -Force
Write-Output "Portable folder: $packagePath"
Write-Output "Share this ZIP: $distributionZip"
