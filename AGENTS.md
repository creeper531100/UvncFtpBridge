# UvncFtpBridge：給 AI／開發者的規則

回覆一律用繁體中文。

## 專案組成

- `UvncFtpBridge.exe`（.NET 9，本機端）：FTP 橋接、Repeater、noVNC、mRemoteNG 主機模式（`--mremoteng`）
- `Jump\UvncFtpJump.exe`（.NET Framework 4.0 WinForms，跳板端，Win7 可用）：常駐、RDP 斷線不結束、
  跟 `--source` 不一樣就自己更新；取代已刪除的 `UvncFtpBridge --remote`（不保留相容）
- `ft.exe`：從 File-Tunnel v3.5.5 **原始碼自編**，不用官方發布的 exe。`Build-Portable.ps1` 會自動 clone 到專案旁的
  `..\File-Tunnel`，並把 `ft\ft.csproj` 的 `net10.0` 改成 `net9.0`

## 建置

- 只要 exe：`Build-CLI.ps1`（→ `dist\UvncFtpBridge.exe`）、`dotnet build Jump\UvncFtpJump.csproj -c Release`
- 整個可攜套件：`Build-Portable.ps1`（需要 UltraVNC 1.4.3.6 已安裝、能上網）
- 改完只列出**要覆蓋的檔案**，不要叫使用者整包重新複製

## mRemoteNG

使用者用**可攜版** mRemoteNG（不看 AppData），原始碼在 https://github.com/creeper531100/MyRemoteNG。
建置 mRemoteNG 一律用 `Release Portable` 組態，細節見那個 repo 的 AGENTS.md。

## 通道怎麼運作（改之前先看）

- 一條跳板一條通道：本機 `ft.exe -D 127.0.0.1:<埠>`（埠從 1082 起找空的）與跳板 `ft.exe` 互相讀寫
  `%TEMP%\UvncFtpBridge\Routes\<路由>\to-remote.dat`、`to-local.dat`，跳板經 `\\tsclient` 存取。路由名稱＝跳板連線的主機名稱／IP。
- 跳板端**不看標記檔**，`ft.exe` 開了就一直跑，RDP 斷線、本機重開、本機關閉都交給 `ft.exe` 自己接回來（它會偵測對方換 session）。
  只有 `ft.exe` 自己結束或關視窗才重開／結束。**不要加回「標記檔變了就關 `ft.exe`」**，那會在通道剛接上時又斷一次。
- 通道資料夾由 start.bat 的 `--source` 推算（`FolderFromSource`），不用掃描。同帳號換一台本機連（路徑不同）時，新啟動的那份接手。
- 本機仍會寫 `active.marker`，只是給舊版跳板端相容用，新版跳板端不讀。
- SOCKS 單次等待（`Route.AttemptTimeoutMs`）一定要**大於 20 秒**：`ft.exe` 本身等跳板撥號的上限是通道逾時 10 秒 × 2。
  曾經砍到 8 秒，結果慢一點的連線永遠等不到結果。沒回覆（通道重啟時請求丟了）才重送；跳板回報的回應碼不重送。

## 看記錄

- **只要看本機 `Logs\launcher.log`**，跳板端的記錄會自動併進來，開頭是 `跳板 [路由]`。跳板端完整記錄在跳板桌面的
  `UvncFtpBridge\Logs\jump.log`，本機副本在 `%TEMP%ﯞploy\jump-<路由>.log`。
- 看不出原因時先把資訊加進記錄，不要一直叫使用者貼指令。下結論前兩端都要看，跳板寫「已連線」不代表連線請求通。
- **跳板的時鐘可能跟本機差很多（南崗快約 15 分鐘）**：`launcher.log` 行首是本機時間，`跳板 [...]` 後面那個時間是跳板的，對時間要用行首。
- 判讀一筆失敗：
  - 跳板有 `Connected to <機台>`：跳板連到機台了，問題在回覆傳回本機這段。
  - 跳板有 `Could not connect to <機台>`：跳板連不到機台，機台或網路的問題。
  - 兩句都沒有，且本機等滿 20 秒報 `回應碼 1`：請求沒到跳板的 `ft.exe`，先懷疑下面的殘留 `ft.exe`。
- 共用檔大小每 15 秒記一次。一個 Ping 是 25 bytes，檔案增長全是 25 的倍數＝只有 Ping，沒有連線請求（Connect 約 39 bytes）。
- `ft.exe` 寫 `MonitorOnlineStatus: 一次只能用一個通訊端位址` ＝ SOCKS 埠被別的程序佔住。

## 踩過的坑：殘留的本機 ft.exe（2026-11-10）

症狀：顯示「跳板通道正常」，但連機台等 20 秒後失敗（`回應碼 1`）；常常一開始連得上，跳板端更新或 `ft.exe` 重開後就全部失敗。

原因：通道主機沒有正常結束時，它的 `ft.exe` 沒被收掉，繼續往同一個 `to-remote.dat` 寫 Ping，也可能佔住 SOCKS 埠。
新的 `ft.exe` 清空檔案後從開頭寫，舊的在很後面的位置繼續寫，檔案長度由舊的決定。
跳板新開的 `ft.exe` 會跳到檔案尾端，只讀得到舊的 Ping，所以顯示在線，新那支送的連線請求卻永遠讀不到。

修法：`ChildProcessJob` 把本機 `ft.exe` 綁在通道主機上（KILL_ON_JOB_CLOSE），主機怎麼死都會跟著結束；
`FileTunnelSession.KillOrphans` 在主機啟動、Repeater 埠綁得上之後清掉同工作階段、旁邊有 `UvncFtpBridge.exe` 的 `ft.exe`。
必須在綁上 Repeater 埠之後才清，綁得上才代表沒有別的通道主機在用。

已查清、不用再猜的：
- 不是多人使用造成的。跳板是 Windows Server，各帳號的工作階段、`\\tsclient`、單一實例鎖都分開，`ft.exe` 在跳板端不佔埠。
- 不是複製太慢。更新中時氣泡和視窗會寫「更新中」。
- 解析度調整造成的 RDP 重連是真的斷線（事件記錄有斷線和重連），不是誤判，靠 `ft.exe` 自己接回來加本機重送處理。

本機重現方式（不用真的跳板）：同一台電腦跑本機端和跳板端，用 `subst` 把暫存資料夾掛成磁碟代號當 `\\tsclient`，
跳板端帶 `--source <代號>:ﯞploy\UvncFtpBridge`，TEMP 指到該資料夾，放一個假機台 TCP 埠。
強制結束 `UvncFtpBridge.exe`、重開，再重開跳板端的 `ft.exe`，舊版就會出現 20 秒 `回應碼 1`。

## 待確認（2026-11-10）

- 修掉殘留 `ft.exe` 的版本只在本機模擬驗證過，還沒有真跳板的結果。
- 上述修改都還沒 commit。`ufb-diag.ps1`、`ufb-diag-oneline.txt` 是一次性的診斷工具，可以不進版控。
- `dist\UltraVNC-WinSCP-FileTunnel-Portable` 裡還是舊版，要發整包前要重跑 `Build-Portable.ps1`。
- 2026-11-10 那個通道主機為什麼沒有正常結束，記錄裡看不出來。
- 南崗 16:37 有一次通道 10 秒沒資料而重啟，原因沒查到。懷疑是解析度調整造成的 RDP 重連，沒有證據。
  可考慮讓有跳板通道在用的 RDP 分頁不要為了調整大小重連，但會改變顯示方式，要先問使用者。
- 討論過把本機端 `ft` 併進 `UvncFtpBridge`：做得到（`RunSession` 是逐通道的實例，全域只有記錄和逾時），
  但跳板端（.NET 4.0）一定還是獨立的 `ft.exe`，也要維護 File-Tunnel 補丁，目前決定先不做。

## 其他

- 不要刪除任何 SSH 金鑰
- 說「好了」之前要實際測過；不能測的部分要明講沒測
