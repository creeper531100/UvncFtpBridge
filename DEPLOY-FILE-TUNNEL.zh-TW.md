# 透過 File-Tunnel 與 RDP 跳板使用 WinSCP

這份套件用 File-Tunnel 的 SOCKS5 `-D` 功能，把本機橋接程式到機台的 VNC TCP 連線放進兩個共用檔案。RDP 的磁碟重新導向讓跳板存取這兩個檔案。無須註冊 mstsc 外掛，也無須在跳板安裝 Python。跳板 IP 不寫在程式裡，依每次工作自行連線。

## 部署一次

1. 本機解壓縮 `dist/UltraVNC-WinSCP-FileTunnel-Portable.zip` 到可寫入的資料夾。`ft.exe`、WinSCP 與橋接程式都在同一套件。
2. 在 mstsc 的「顯示選項 → 本機資源 → 詳細資料」勾選本機 **C:** 磁碟。每次連線若選用不同的 RDP 設定檔，都要確認此項；修改後請重新建立 RDP 連線。
3. 把套件中的 `ft.exe`、`Start-FileTunnel-Remote.cmd` 與 `Start-FileTunnel-Remote.ps1` 複製到**每一台要用的跳板**的同一資料夾。無須在跳板安裝或註冊，直接執行即可。

## 每次使用

1. **本機**雙擊 `Start-WinSCP-FileTunnel.vbs`；若禁用 VBS，使用 `.cmd`。WinSCP 先開，File-Tunnel 在本機背景監聽 `127.0.0.1:1081`，FTP 橋接也同時啟動。
2. **本機**自行用 mstsc 連到這次選用的跳板。啟動檔不會替你開 RDP。
3. **跳板**確認檔案總管可開啟 `\\tsclient\C`，再雙擊 `Start-FileTunnel-Remote.cmd`，保持命令視窗開啟。遠端腳本會找出本機 `%TEMP%\UvncFtpBridge\FileTunnel`；`\\tsclient\C` 只是重新導向磁碟入口，資料檔實際存在本機 TEMP。路徑沒有 `$`。
4. **本機 WinSCP**填要連的**機台 IP**、VNC 埠（通常 5900）及 VNC 密碼，按登入。機台 IP 可以不同於跳板 IP，也不必是 `127.0.0.1`。如果先登入失敗，跳板端啟動後再按登入。

若本機系統磁碟不是 C，從跳板命令列用 `Start-FileTunnel-Remote.cmd D` 等方式指定磁碟代號。若本機 TEMP 不是 Windows 預設的 `%LOCALAPPDATA%\Temp`，可以傳入完整的重新導向路徑，例如 `Start-FileTunnel-Remote.cmd "\\tsclient\C\自訂TEMP\UvncFtpBridge\FileTunnel"`。遠端腳本預設搜尋 C 槽的使用者 TEMP。

一次只使用一個跳板。切換時先關閉舊跳板的 File-Tunnel 命令視窗，再在新跳板啟動。關閉本機由此入口開啟的 WinSCP，會一併關閉本機 File-Tunnel 與 FTP 橋接；跳板腳本偵測到本機結束後也會停止。暫存資料檔會嘗試刪除。

## 排查

- 跳板看不到 `\\tsclient\C`：確認 mstsc 磁碟重新導向已勾選並重新連線；群組原則也可能禁止磁碟重新導向。
- 本機 1081 埠被其他程式佔用：關閉舊 File-Tunnel 程序，重新從啟動檔開啟。啟動記錄在套件的 `Logs/launcher.log`。
- WinSCP 顯示代理或目標連線錯誤：確認跳板端命令視窗仍在執行，而且跳板能連到機台的 VNC 埠。
- 這個通道依賴現有 RDP 工作階段與磁碟重新導向。本機關閉 WinSCP 或 RDP 斷線時，跳板端會自動回到等待；重新連上後直接重試 WinSCP 登入即可，不必重啟跳板端。

這個流程使用 File-Tunnel 官方未修改的 Windows x64 執行檔；專案與授權資訊在套件的 `ThirdParty.txt`。
