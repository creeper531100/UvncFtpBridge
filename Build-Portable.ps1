[CmdletBinding()]
param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$rootPath = $PSScriptRoot
$cachePath = Join-Path $rootPath '.portable-cache'
$packageName = if ($FrameworkDependent) { 'UltraVNC-WinSCP-Portable-FrameworkDependent' } else { 'UltraVNC-WinSCP-Portable' }
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
& (Join-Path $rootPath 'Build-CLI.ps1') -FrameworkDependent:$FrameworkDependent
$cliFiles = @(if ($FrameworkDependent) {
    Get-ChildItem -LiteralPath (Join-Path $rootPath 'dist\FrameworkDependent') -File
} else {
    Get-Item -LiteralPath (Join-Path $rootPath 'dist\UvncFtpBridge.exe')
})
foreach ($cliFile in $cliFiles) { Copy-Item -LiteralPath $cliFile.FullName -Destination $packagePath }
Copy-Item -LiteralPath (Join-Path $rootPath 'Portable\WinSCP.ini') -Destination $clientPath
foreach ($item in @('Start-WinSCP.vbs','Start-WinSCP.cmd','使用說明.txt','ThirdParty.txt')) {
    Copy-Item -LiteralPath (Join-Path $rootPath "Portable\$item") -Destination $packagePath
}
if ($FrameworkDependent) {
    $instructionsPath = Join-Path $packagePath '使用說明.txt'
    $instructions = Get-Content -LiteralPath $instructionsPath -Raw -Encoding utf8
    $runtimeNote = "精簡版：使用前須安裝 .NET 9 Runtime x64（不需 SDK）。`r`n下載：https://dotnet.microsoft.com/en-us/download/dotnet/9.0`r`nEXE、DLL 與 JSON 檔案須保留在同一資料夾。`r`n`r`n"
    Set-Content -LiteralPath $instructionsPath -Value ($runtimeNote + $instructions) -Encoding utf8
}
$distributionZip = Join-Path $rootPath "dist\$packageName.zip"
# Only ship official client files, the CLI, the clean INI template and the entry points.
$files = @(
    (Join-Path $packagePath 'Start-WinSCP.vbs'),
    (Join-Path $packagePath 'Start-WinSCP.cmd'),
    (Join-Path $packagePath '使用說明.txt'),
    (Join-Path $packagePath 'ThirdParty.txt'),
    $clientPath
)
$files += @($cliFiles | ForEach-Object { Join-Path $packagePath $_.Name })
Compress-Archive -LiteralPath $files -DestinationPath $distributionZip -Force
Write-Output "Portable folder: $packagePath"
Write-Output "Share this ZIP: $distributionZip"
