# UltraVNC FTP Bridge — CLI 與 WinSCP ＋ UltraVNC 可攜套件

新增的真正 SSH 經 VNC / File-Tunnel 原型、使用條件與驗證範圍，見 [VNC-SSH-TUNNEL.zh-TW.md](VNC-SSH-TUNNEL.zh-TW.md)。舊版 FxSsh shell 已備份於 `backup/vnc-ssh-fxssh`。

這個專案只有 C# CLI，沒有自製 UI，也不包含 WinSCP 原始碼或 C++Builder 建置流程。可攜套件使用官方未修改的 WinSCP 6.7、預設 INI 與官方 UltraVNC Viewer 1.4.3.6。

## 給其他事業處使用

分享 `dist/UltraVNC-WinSCP-FileTunnel-Portable.zip`，對方使用 Windows x64，完整解壓縮到可寫入的資料夾後，只有兩個啟動檔，都會同時開 WinSCP（傳檔）與 UltraVNC Viewer（看畫面）：

- **Start-Direct.cmd**：直連，本機連得到機台時用。
- **Start-FileTunnel.cmd**：經 RDP 跳板，開 File-Tunnel（`ft.exe`）。

1. 雙擊其中一個。命令視窗開著就一直可用，關閉 WinSCP 或 Viewer 不影響（可把兩者釘在工作列）；按 Q 或關閉命令視窗才結束。
2. WinSCP 新增站台填 **真實機台 IP**、**VNC 埠**（預設 5900）、**VNC 密碼**，按登入。FTP、不加密、使用者名稱 `ultravnc` 及 FTP 代理都已預設好。
3. UltraVNC Viewer 已預選「Repeater」，在伺服器欄填機台 IP，按 Connect。

不需要安裝 WinSCP、UltraVNC、.NET 或 C++Builder。設定保存在套件的 `WinSCP/WinSCP.ini` 與 `UltraVNC/options.vnc`；套件不預存任何機台 IP 或密碼。啟動記錄在 `Logs/launcher.log`，不隨發行 ZIP 打包。
### 精簡版（不包含 .NET 執行階段）

另外提供 `dist/UltraVNC-WinSCP-FileTunnel-Portable-FrameworkDependent/` 與同名 ZIP。雙擊入口、代理設定與站台儲存方式相同，但使用電腦需先安裝 **.NET 9 Runtime x64**，不用 SDK。下載：https://dotnet.microsoft.com/en-us/download/dotnet/9.0

精簡版的 EXE、DLL、`.deps.json` 與 `.runtimeconfig.json` 必須一起保留。單獨 CLI 建置則在 `dist/FrameworkDependent/`。原本的自含套件仍保留。

要沿用先前儲存的站台，可在兩份 WinSCP 都關閉時，將舊套件的 `WinSCP/WinSCP.ini` 複製到精簡版的相同位置；下次從啟動檔執行會自動更新代理埠。

## 運作方式

```text
WinSCP 主機欄：機台真實 IP / VNC 埠
        ↓ WinSCP 內建 FTP Proxy
本機 CLI：127.0.0.1 / 系統分配的可用埠
        ↓ UltraVNC RFB 檔案傳輸
機台 UltraVNC Server
```

### 經 File-Tunnel 與 RDP 磁碟重新導向連內網機台

完整步驟見 [File-Tunnel 部署教學](DEPLOY-FILE-TUNNEL.zh-TW.md)。File-Tunnel 使用 RDP 已提供的本機磁碟重新導向，無須註冊 mstsc 外掛；本機與跳板各執行一份官方 `ft.exe`。

1. 本機雙擊 **Start-FileTunnel.cmd**（`UvncFtpBridge.exe --open --file-tunnel`）。WinSCP 與 Viewer 立即開啟，本機 SOCKS5 `127.0.0.1:1081` 等待跳板。
2. 自行用 mstsc 連到這次的跳板，連線設定須勾選本機 C 槽重新導向。
3. 在跳板執行 **Start-FileTunnel-Remote.cmd**（與 `UvncFtpBridge.exe`、`ft.exe` 放同一資料夾；它執行 `UvncFtpBridge.exe --remote`，不需 PowerShell，Windows 7 也可用）。保持視窗開啟。
4. WinSCP 站台或 Viewer 填**目標機台的真實 IP**，連線。

```text
WinSCP（機台 IP）→ 本機 FTP 橋接 ─┐
Viewer（機台 IP）→ 本機 Repeater ─┴→ 本機 File-Tunnel SOCKS5
    → \\tsclient\C\... 共用檔案 → 跳板 File-Tunnel → 機台 UltraVNC Server
```

啟動檔不指定跳板 IP，也不自動開 RDP。代理、磁碟重新導向或跳板不可用時會報錯，**不會自動直連機台**。`Start-Direct.cmd` 直連；若設了 `UVNC_SOCKS5_PROXY`，直連模式改經該 SOCKS5 代理，一般 CLI 或手動 `--ftp` 模式也一樣。

CLI 啟動時自動更新 INI 的本機代理埠，包含原先儲存的 UltraVNC FTP 站台；主機 IP 與 VNC 密碼仍由 WinSCP 管理。橋接使用系統分配的可用埠，不會占用固定 2221 或與手動橋接衝突。同一份資料夾同時只執行一份服務；再開一次啟動檔只會再開 WinSCP 與 Viewer。
檔案協定仍顯示原版 **FTP**。機台需啟用 UltraVNC File Transfer，並使用完整權限的 VNC 密碼。沿用現有協定限制：不支援 MS-Logon、SecureVNC 插件；機台端 VNC 傳輸未加密。

## CLI

```text
UvncFtpBridge ls    <IP或IP:埠> [遠端資料夾]
UvncFtpBridge get   <IP或IP:埠> <遠端路徑> [本機路徑]
UvncFtpBridge put   <IP或IP:埠> <本機路徑> <遠端路徑>
UvncFtpBridge rm    <IP或IP:埠> <遠端路徑>
UvncFtpBridge mkdir <IP或IP:埠> <遠端資料夾>
UvncFtpBridge mv    <IP或IP:埠> <舊路徑> <新路徑>
UvncFtpBridge winscp
```

密碼可從 `UVNC_PASSWORD` 環境變數讀取，或以 `-p` 指定；未提供時會提示輸入。其他選項：`--vnc-port`、`-r`（遞迴）、`--json`（ls）、`-q`、`-v`、`--log`。

結果輸出 stdout，記錄及錯誤輸出 stderr。結束碼：0 成功、1 失敗、2 參數錯誤。無參數只顯示說明。

手動開 FTP 代理：

```powershell
.\dist\UvncFtpBridge.exe --ftp --port 2221
```

支援 USER `機台IP[:VNC埠]` 或 WinSCP 代理傳送的 `使用者@機台IP[:VNC埠]`。`--target` 可固定機台、`--port 0` 由系統分配埠、`--ready-stdout` 回報 `UVNCFTP/1 <實際埠>`。`winscp` 指令則自動處理代理與旁邊的原版可攜 WinSCP。

## 建置

開發環境只需 .NET 9 SDK：

```powershell
.\Build-CLI.ps1       # 發行自含執行檔至 dist
.\Build-Portable.ps1  # 下載並驗證官方 WinSCP、繁中翻譯，建立可攜 ZIP
.\Build-CLI.ps1 -FrameworkDependent       # 精簡 CLI，不內含 .NET
.\Build-Portable.ps1 -FrameworkDependent  # 精簡 WinSCP 可攜套件，獨立資料夾及 ZIP
```

`Build-Portable.ps1` 固定使用 WinSCP 6.7，核對官方 SHA-256 及 EXE 數位簽章。INI、啟動檔和使用說明的原始模板在 `Portable/`。

| 資料夾 | 內容 |
|---|---|
| Cli | CLI 指令及可攜版啟動整合 |
| Client | 機台連線、路徑與檔案操作 |
| Rfb | UltraVNC 連線及檔案傳輸協定 |
| Ftp | FTP 代理與 UltraVNC 轉換 |
| Portable | 原版 WinSCP 的 INI、啟動檔與說明模板 |
| tests | 本機模擬與實機只讀測試 |

## 驗證

```powershell
.\dist\UvncFtpBridge.exe --selftest
python tests/integration.py --bridge dist/UvncFtpBridge.exe --winscp dist/UltraVNC-WinSCP-FileTunnel-Portable/WinSCP/WinSCP.com
python tests/portable.py
```

已驗證多機台隔離、驗證失敗與重新登入、中文檔名、二進位上下載、改名、刪除及目錄操作。可攜版測試涵蓋直接繼承 INI 代理設定、中文／空白路徑、已儲存站台跨次啟動、關閉後釋放代理埠。

2026-10-20 已用真實 UltraVNC 與本機實際 IP 完成可攜套件的登入、磁碟列出與下載 SHA-256 比對；測試密碼未寫入專案或發行套件。

官方 WinSCP 的授權、下載來源及原始碼取得網址保存在可攜套件 `ThirdParty.txt` 與 `WinSCP/license.txt`。
