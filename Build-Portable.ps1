[CmdletBinding()]
param(
    [switch]$FrameworkDependent,
    # dist 裡的 noVNC 有手動改過的檔案時預設停下來不覆蓋；確定要丟掉那些修改才加這個。
    [switch]$OverwriteNoVnc,
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
# ft.exe 從原始碼自己編（不用官方發布的 exe）：
#   這個專案旁邊沒有 File-Tunnel 就自動 git clone v3.5.5（放在 ..\File-Tunnel）。
#   官方 v3.5.5 目標是 net10.0；這裡用 .NET 9 SDK 編，所以自動把 ft\ft.csproj 改成 net9.0（只改這一行，程式碼不動）。
#   發布方式跟官方 build-release.ps1 說的一樣：Release、自包含、單一檔案、修剪（另加壓縮）。
$ftTag = 'v3.5.5'
$ftSource = Join-Path (Split-Path -Parent $rootPath) 'File-Tunnel'
$ftProject = Join-Path $ftSource 'ft\ft.csproj'
if (-not (Test-Path -LiteralPath $ftProject)) {
    & git clone --depth 1 --branch $ftTag https://github.com/fiddyschmitt/File-Tunnel.git $ftSource
    if ($LASTEXITCODE -ne 0) { throw "File-Tunnel clone failed: $ftSource" }
}
$ftCsproj = [IO.File]::ReadAllText($ftProject)
if ($ftCsproj.Contains('<TargetFramework>net10.0</TargetFramework>')) {
    [IO.File]::WriteAllText($ftProject, $ftCsproj.Replace('<TargetFramework>net10.0</TargetFramework>', '<TargetFramework>net9.0</TargetFramework>'), (New-Object Text.UTF8Encoding $true))
}
$ftOutput = Join-Path $rootPath 'dist\File-Tunnel'
& dotnet publish $ftProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $ftOutput
if ($LASTEXITCODE -ne 0) { throw 'File-Tunnel build failed (ft.csproj must target net9.0 for the .NET 9 SDK).' }
Copy-Item -LiteralPath (Join-Path $ftOutput 'ft.exe') -Destination (Join-Path $packagePath 'ft.exe') -Force
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
# 跳板端 UvncFtpJump.exe（.NET Framework 4，Win7 跳板可用；不再用 UvncFtpBridge.exe --remote）
$jumpOutput = Join-Path $rootPath 'dist\Jump'
& dotnet build (Join-Path $rootPath 'Jump\UvncFtpJump.csproj') -c Release -o $jumpOutput
if ($LASTEXITCODE -ne 0) { throw 'UvncFtpJump build failed.' }
Copy-Item -LiteralPath (Join-Path $jumpOutput 'UvncFtpJump.exe') -Destination $packagePath -Force
# noVNC (served by --open for mRemoteNG tabs): official release beside the exe in noVNC.
# Ours: defaults.json (autoconnect + scale to the tab), the UltraVNC cursor patch to core/rfb.js and the
# Windows clipboard sync patch to app/ui.js below.
$noVncVersion = '1.7.0'
$noVncHash = 'D55E9CCE9A611294DF33C7200F32F35108652DE2D86A468BEBCFC6F2A1C9A196'
$noVncZip = Join-Path $cachePath "noVNC-$noVncVersion.zip"
if (-not (Test-Path -LiteralPath $noVncZip)) {
    Invoke-WebRequest -Uri "https://github.com/novnc/noVNC/archive/refs/tags/v$noVncVersion.zip" -OutFile $noVncZip
}
if ((Get-FileHash -LiteralPath $noVncZip -Algorithm SHA256).Hash -ne $noVncHash) {
    throw 'noVNC SHA-256 does not match the pinned release checksum.'
}
$noVncExtract = Join-Path $cachePath "noVNC-$noVncVersion"
Remove-Item -LiteralPath $noVncExtract -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath $noVncZip -DestinationPath $noVncExtract -Force
$noVncSource = Join-Path $noVncExtract "noVNC-$noVncVersion"
$noVncPath = Join-Path $packagePath 'noVNC'
# 先在暫存區組好，確認不會蓋掉 dist 裡手動改過的檔案，才換上去。
$noVncStage = Join-Path $cachePath 'noVNC-stage'
Remove-Item -LiteralPath $noVncStage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $noVncStage -Force | Out-Null
# package.json is fetched by vnc.html to show the noVNC version.
foreach ($item in @('vnc.html','app','core','vendor','mandatory.json','package.json','LICENSE.txt','AUTHORS')) {
    Copy-Item -LiteralPath (Join-Path $noVncSource $item) -Destination $noVncStage -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $rootPath 'Portable\noVNC-defaults.json') -Destination (Join-Path $noVncStage 'defaults.json') -Force
# UltraVNC server only sends cursor shapes (-239) when the client also asks for PointerPos (-232)
# (vncclient.cpp SetEncodings: no m_use_PointerPos -> EnableRichCursor(FALSE)), otherwise the cursor is
# drawn into the remote framebuffer and lags. Patch noVNC to request -232 and skip its header-only rects.
$rfbJsPath = Join-Path $noVncStage 'core\rfb.js'
$rfbJs = [IO.File]::ReadAllText($rfbJsPath)
$encAnchor = 'encs.push(encodings.pseudoEncodingCursor);'
$rectAnchor = 'case encodings.pseudoEncodingVMwareCursor:'
if (-not $rfbJs.Contains($encAnchor) -or -not $rfbJs.Contains($rectAnchor)) {
    throw 'noVNC rfb.js changed: UltraVNC PointerPos patch anchors not found.'
}
$rfbJs = $rfbJs.Replace($encAnchor, "$encAnchor encs.push(-232); /* UltraVNC PointerPos */")
$rfbJs = $rfbJs.Replace($rectAnchor, "case -232: return true; /* UltraVNC PointerPos */`n            $rectAnchor")
[IO.File]::WriteAllText($rfbJsPath, $rfbJs, (New-Object Text.UTF8Encoding $false))
# noVNC only puts the server clipboard into its side panel and never touches the system clipboard.
# Sync it with the Windows clipboard (mRemoteNG's WebView2 tab auto-allows the clipboard-read permission):
#   server -> PC: navigator.clipboard.writeText when the server clipboard arrives (kept until focus if the page is not focused);
#   PC -> server: navigator.clipboard.readText when the page gets focus or connects, sent only when it changed.
$uiJsPath = Join-Path $noVncStage 'app\ui.js'
$uiJs = [IO.File]::ReadAllText($uiJsPath)
$clipAnchors = @{
    Receive  = "document.getElementById('noVNC_clipboard_text').value = e.detail.text;"
    Methods  = '    clipboardSend() {'
    Handlers = ".addEventListener('change', UI.clipboardSend);"
    Connect  = 'UI.connected = true;'
}
foreach ($anchor in $clipAnchors.Values) {
    if (([regex]::Matches($uiJs, [regex]::Escape($anchor))).Count -ne 1) {
        throw "noVNC ui.js changed: clipboard sync patch anchor not found exactly once: $anchor"
    }
}
$clipMethods = @'
    // mRemoteNG clipboard sync (see Build-Portable.ps1)
    sysClipLast: null,
    sysClipPending: null,

    sysClipWrite(text) {
        UI.sysClipLast = text;
        if (!navigator.clipboard || !navigator.clipboard.writeText) return;
        navigator.clipboard.writeText(text).catch(() => { UI.sysClipPending = text; });
    },

    sysClipRead() {
        if (!navigator.clipboard) return;
        if (UI.sysClipPending !== null) {
            const pending = UI.sysClipPending;
            UI.sysClipPending = null;
            UI.sysClipWrite(pending);
            return;
        }
        if (!UI.rfb || !UI.connected || !navigator.clipboard.readText) return;
        navigator.clipboard.readText().then((text) => {
            if (typeof text !== 'string' || text === '' || text === UI.sysClipLast) return;
            UI.sysClipLast = text;
            document.getElementById('noVNC_clipboard_text').value = text;
            UI.rfb.clipboardPasteFrom(text);
        }).catch(() => {});
    },

'@.Replace("`r`n", "`n")
$uiJs = $uiJs.Replace($clipAnchors.Receive, $clipAnchors.Receive + " UI.sysClipWrite(e.detail.text); /* mRemoteNG clipboard sync */")
$uiJs = $uiJs.Replace($clipAnchors.Methods, $clipMethods + $clipAnchors.Methods)
$uiJs = $uiJs.Replace($clipAnchors.Handlers, $clipAnchors.Handlers +
    "`n        window.addEventListener('focus', UI.sysClipRead); /* mRemoteNG clipboard sync */" +
    "`n        document.addEventListener('visibilitychange', () => { if (!document.hidden) UI.sysClipRead(); });")
$uiJs = $uiJs.Replace($clipAnchors.Connect, $clipAnchors.Connect + " UI.sysClipRead(); /* mRemoteNG clipboard sync */")
[IO.File]::WriteAllText($uiJsPath, $uiJs, (New-Object Text.UTF8Encoding $false))
# 自己的 noVNC 修改放 Portable\noVNC-overrides\（同樣的相對路徑），每次打包都會蓋在官方檔案上，不會被沖掉。
$noVncOverrides = Join-Path $rootPath 'Portable\noVNC-overrides'
if (Test-Path -LiteralPath $noVncOverrides) {
    Get-ChildItem -LiteralPath $noVncOverrides -Force | Copy-Item -Destination $noVncStage -Recurse -Force
    Write-Output "noVNC overrides applied: $noVncOverrides"
}
# dist 現有的 noVNC 是否被手動改過：跟上次打包寫出的清單（相對路徑|SHA256）比；沒有清單時跟這次組出來的比。
# 用清單比才分得出「腳本的補丁變了」跟「有人手改了 dist」。
$noVncManifest = Join-Path $cachePath 'noVNC-manifest.txt'
function Get-NoVncHashes([string]$folder) {
    $base = (Resolve-Path -LiteralPath $folder).Path.TrimEnd('\') + '\'
    $map = @{}
    Get-ChildItem -LiteralPath $folder -Recurse -File -Force | ForEach-Object {
        $map[$_.FullName.Substring($base.Length)] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $map
}
if ((Test-Path -LiteralPath $noVncPath) -and -not $OverwriteNoVnc) {
    $existing = Get-NoVncHashes $noVncPath
    $expected = @{}
    if (Test-Path -LiteralPath $noVncManifest) {
        foreach ($line in [IO.File]::ReadAllLines($noVncManifest)) {
            $parts = $line.Split('|')
            if ($parts.Count -eq 2) { $expected[$parts[0]] = $parts[1] }
        }
    } else {
        $expected = Get-NoVncHashes $noVncStage
    }
    $modified = @($existing.Keys | Where-Object { -not $expected.ContainsKey($_) -or $expected[$_] -ne $existing[$_] } | Sort-Object)
    if ($modified.Count -gt 0) {
        throw ("dist 的 noVNC 有手動修改，這次不覆蓋（$noVncPath）：`n  " + ($modified -join "`n  ") +
               "`n要保留：把這些檔案照相對路徑放進 Portable\noVNC-overrides\ 再打包。" +
               "`n確定要丟掉：加 -OverwriteNoVnc。")
    }
}
Remove-Item -LiteralPath $noVncPath -Recurse -Force -ErrorAction SilentlyContinue
Move-Item -LiteralPath $noVncStage -Destination $noVncPath
$written = Get-NoVncHashes $noVncPath
[IO.File]::WriteAllLines($noVncManifest, [string[]]($written.Keys | Sort-Object | ForEach-Object { "$_|$($written[$_])" }))
Copy-Item -LiteralPath (Join-Path $rootPath 'Portable\WinSCP.ini') -Destination $clientPath
foreach ($item in @('Start-Direct.cmd','Start-FileTunnel.cmd','Start-FileTunnel-Remote.cmd','ft-options.txt','使用說明.txt','ThirdParty.txt','File-Tunnel-LICENSE.md')) {
    Copy-Item -LiteralPath (Join-Path $rootPath "Portable\$item") -Destination $packagePath
}
# Drop entry points from earlier builds: two launchers (direct / File-Tunnel) now open WinSCP and UltraVNC together,
# and the jump-host side runs as UvncFtpJump.exe instead of a PowerShell script.
foreach ($old in @('Start-WinSCP.vbs','Start-WinSCP.cmd','Start-WinSCP-FileTunnel.vbs','Start-WinSCP-FileTunnel.cmd','Start-VNC-FileTunnel.cmd','Start-FileTunnel-Remote.ps1')) {
    Remove-Item -LiteralPath (Join-Path $packagePath $old) -ErrorAction SilentlyContinue
}
# Leftovers of the removed VNC-SSH gateway and of the earlier hand-written noVNC page.
foreach ($old in @('OpenSSH','VNC-SSH-TUNNEL.zh-TW.md','vnc.html')) {
    Remove-Item -LiteralPath (Join-Path $packagePath $old) -Recurse -Force -ErrorAction SilentlyContinue
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
    (Join-Path $packagePath 'ft.exe'),
    (Join-Path $packagePath 'UvncFtpJump.exe'),
    $noVncPath,
    $clientPath,
    $viewerPath
)
$files += @($cliFiles | ForEach-Object { Join-Path $packagePath $_.Name })
Compress-Archive -LiteralPath $files -DestinationPath $distributionZip -Force
Write-Output "Portable folder: $packagePath"
Write-Output "Share this ZIP: $distributionZip"
