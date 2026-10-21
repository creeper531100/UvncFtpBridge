# 透過 File-Tunnel 與 RDP 跳板使用 WinSCP 與 UltraVNC

這份套件用 File-Tunnel 的 SOCKS5 `-D` 功能，把本機到機台的 VNC TCP 連線放進兩個共用檔案。RDP 的磁碟重新導向讓跳板存取這兩個檔案。無須註冊 mstsc 外掛，也無須在跳板安裝 Python、PowerShell 新版或 .NET。跳板 IP 不寫在程式裡，依每次工作自行連線。

本機的 `Start-FileTunnel.cmd` 同時開 WinSCP（傳檔）與 UltraVNC Viewer（看畫面），兩者共用同一條通道：

```text
WinSCP（機台 IP）        → 本機 FTP 橋接 ─┐
UltraVNC Viewer（機台 IP）→ 本機 Repeater ─┴→ 本機 ft.exe SOCKS5 127.0.0.1:1081
  → 共用檔案（RDP 磁碟重新導向）→ 跳板 ft.exe → 機台 VNC 5900
```

UltraVNC Viewer 不支援 SOCKS5，但支援 UltraVNC Repeater；本機在 `127.0.0.1:5999` 扮演 Repeater（Mode I），Viewer 把機台位址交給它，它再經通道轉過去。

## 部署一次

1. 本機解壓縮 `dist/UltraVNC-WinSCP-FileTunnel-Portable.zip` 到可寫入的資料夾。`ft.exe`、WinSCP、UltraVNC Viewer 與橋接程式都在同一套件。
2. 在 mstsc 的「顯示選項 → 本機資源 → 詳細資料」勾選本機 **C:** 磁碟。每次連線若選用不同的 RDP 設定檔，都要確認此項；修改後請重新建立 RDP 連線。
3. 把套件中的 `UvncFtpBridge.exe`、`ft.exe` 與 `Start-FileTunnel-Remote.cmd` 複製到**每一台要用的跳板**的同一資料夾。跳板端由 `UvncFtpBridge.exe --remote` 處理，不需安裝或註冊，Windows 7 跳板也可用。
4. （可選）把套件的 `WinSCP\WinSCP.exe` 與 `UltraVNC\vncviewer.exe` 釘選到工作列。

## 每次使用

1. **本機**雙擊 `Start-FileTunnel.cmd`。命令視窗會啟動 File-Tunnel、FTP 橋接與 Repeater，並開好 WinSCP 與 UltraVNC Viewer（Viewer 已預選「Repeater」）。
2. **本機**自行用 mstsc 連到這次選用的跳板。啟動檔不會替你開 RDP。
3. **跳板**確認檔案總管可開啟 `\\tsclient\C`，再雙擊 `Start-FileTunnel-Remote.cmd`，保持命令視窗開啟。它會找出本機 `%TEMP%\UvncFtpBridge\FileTunnel`；`\\tsclient\C` 只是重新導向磁碟入口，資料檔實際存在本機 TEMP。路徑沒有 `$`。
4. **WinSCP** 填機台 IP、VNC 埠（通常 5900）及 VNC 密碼，按登入。**Viewer** 在伺服器欄填機台 IP（非 5900 埠寫 `10.1.2.3::5901`），按 Connect，輸入 VNC 密碼。機台 IP 可以不同於跳板 IP。
5. 命令視窗開著就一直可用；關閉 WinSCP 或 Viewer 不影響，可從工作列再開，或在命令視窗按 W／V。用完按 Q 或關閉命令視窗，`ft.exe` 等會一併結束。

File-Tunnel 要等跳板端連上才開始接受連線（log 出現 `Started SOCKS proxy`），通常要十幾秒。這段期間在 Viewer 按 Connect，Repeater 會等通道就緒（最多 30 秒）再接過去；WinSCP 若先登入失敗，跳板端連上後再按一次。

若本機系統磁碟不是 C，從跳板命令列用 `Start-FileTunnel-Remote.cmd D` 等方式指定磁碟代號。若本機 TEMP 不是 Windows 預設的 `%LOCALAPPDATA%\Temp`，可以傳入完整的重新導向路徑，例如 `Start-FileTunnel-Remote.cmd "\\tsclient\C\自訂TEMP\UvncFtpBridge\FileTunnel"`。

一次只使用一個跳板。切換時先關閉舊跳板的命令視窗，再在新跳板啟動。本機結束時，跳板端會偵測到並回到等待，下次本機重開即自動接上；暫存資料檔會嘗試刪除。

套件內的 Viewer 是可攜模式，設定存在 `UltraVNC\options.vnc`；每次啟動都會把 `UseProxy`、`proxyhost`、`proxyport` 改回這個 Repeater（連過的機台在 `%APPDATA%\UltraVNC\<機台>-<埠>.vnc` 的設定也一併改），其餘設定保留。若用電腦已安裝的 Viewer，啟動檔不會改它的設定，請在連線視窗自行選「Repeater」並填 `127.0.0.1:5999`。

本機連得到機台時，改用 `Start-Direct.cmd`：一樣開 WinSCP 與 Viewer，但不開 File-Tunnel、直接連機台。

## 速度調整

`ft.exe` 本身在本機磁碟上可達每秒數十 MB；經跳板時的瓶頸是 RDP 磁碟重新導向（`\\tsclient`），每次寫入共用檔都要經 RDP 來回一次。檔案傳輸會自動要求 zlib 壓縮，文字檔（CSV、log）實際傳得比較快。

要調整 `ft.exe`，在 `UvncFtpBridge.exe` 旁放 `ft-options.txt`，內容會附加到 `ft.exe` 參數（空白分隔，`#` 開頭為註解）。本機與跳板各自讀自己那份：下載主要受跳板端那份影響（跳板寫 `to-local.dat`），上傳主要受本機那份影響。可以試：

```text
--read-duration 200
--citrix
```

`--read-duration` 越大、每次寫入的資料越多、寫入次數越少，但 VNC 畫面反應會稍慢。改完重開對應的啟動檔，用 WinSCP 傳同一個大檔比較速度。

## 排查

- 跳板看不到 `\\tsclient\C`：確認 mstsc 磁碟重新導向已勾選並重新連線；群組原則也可能禁止磁碟重新導向。
- 本機 1081 埠或 5999 埠被其他程式佔用：關閉舊的命令視窗或程序，重新從啟動檔開啟。啟動記錄在套件的 `Logs/launcher.log`。
- WinSCP 或 Viewer 連線失敗：看 `Logs/launcher.log`。「回應碼 5」表示跳板去連機台被拒絕，請確認機台的 UltraVNC Server 有在執行、防火牆允許 5900，而且跳板連得到該機台。
- 這個通道依賴現有 RDP 工作階段與磁碟重新導向。RDP 斷線時跳板端會自動回到等待；重新連上後直接重試即可，不必重啟跳板端。

這個流程使用 File-Tunnel 官方未修改的 Windows x64 執行檔，以及官方未修改、有數位簽章的 UltraVNC Viewer 1.4.3.6；專案與授權資訊在套件的 `ThirdParty.txt`。
