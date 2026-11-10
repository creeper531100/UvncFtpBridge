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

## 其他

- 不要刪除任何 SSH 金鑰
- 說「好了」之前要實際測過；不能測的部分要明講沒測
