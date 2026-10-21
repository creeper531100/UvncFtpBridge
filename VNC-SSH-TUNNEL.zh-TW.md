# 可攜 OpenSSH 經 VNC / File-Tunnel

舊版 FxSsh 與逐行 PowerShell driver 保存在本機分支 `backup/vnc-ssh-fxssh`（`b0a321d`）。新版不實作 SSH 協定或 shell。

```text
套件 OpenSSH/ssh.exe
  → 本機隨機 loopback TCP 埠
  → 本機官方 ft.exe --upload-download
  → UvncFtpBridge 經 VNC 搬運資料檔與刪除確認
  → 機台官方 ft.exe --upload-download
  → 機台隨機 loopback TCP 埠
  → 本次上傳的官方 OpenSSH sshd → 真正 cmd / PowerShell
```

## 用法

機台不必預先安裝 sshd，也不必開放外部的 22 埠。第一次必須有未鎖定、可操作的桌面：VNC 會用 Win+R 開一個 cmd 視窗，再逐字把啟動指令打進去執行。因為是逐字模擬鍵盤，機台若有中文輸入法卡在英數以外模式，可能干擾輸入。

```cmd
UvncFtpBridge.exe --ssh Ussr@192.168.1.45 -p VNC密碼
UvncFtpBridge.exe --ssh Ussr@192.168.1.45 -p VNC密碼 "hostname & whoami"
UvncFtpBridge.exe --ssh Ussr@192.168.1.45 -p VNC密碼 --admin
```

帳號必須是機台目前桌面的 Windows 帳號。`--admin` 會在機台跳 UAC，從 VNC 按「是」之後才繼續，SSH 取得同一帳號的系統管理員權限。等待 UAC 不設逾時，Ctrl+C 可取消。

Windows OpenSSH 的互動 debug 模式只支援目前使用者及金鑰登入。因此每次連線由本機產生臨時 Ed25519 金鑰，只把公鑰上傳到獨立的 session 目錄，不修改使用者原有的 authorized_keys。私鑰不會上傳，本機臨時金鑰保留在 session 目錄，不會自動刪除。也可用 `--identity 私鑰路徑`，必須同時提供旁邊的 `.pub` 檔。

機台自行產生本次主機金鑰，本機經 VNC 取得主機公鑰後以嚴格檢查方式連線，不寫入使用者永久的 known_hosts。這條首次信任仍以 VNC 的存取權限為基礎。

官方限制說明：https://github.com/PowerShell/Win32-OpenSSH/wiki/Troubleshooting-Steps

## 部署與結束

套件 `OpenSSH` 內包含 ssh、sshd、sshd-auth、sshd-session、ssh-shellhost、ssh-keygen、sftp-server 與 libcrypto.dll。來自固定版本的官方 ZIP，建置腳本驗證 ZIP SHA-256 與 Microsoft 簽章。不會回退使用系統的 ssh。

每次上傳到機台使用者的 `%TEMP%\UvncFtpBridge\SshTunnel\<隨機ID>`：經 VNC 檔案傳輸找 `<磁碟>:\Users\<帳號>\AppData\Local\Temp`，不用剪貼簿。start.ps1 啟動時確認自己就在該使用者的 %TEMP% 底下，否則報錯。金鑰、sshd_config、記錄全部只放這個資料夾，啟動後限制 ACL 給同一使用者、SYSTEM、Administrators。sshd 只聽 127.0.0.1 的隨機埠，不安裝服務、不新增防火牆規則、不改原本 sshd。debug 模式只處理一條 SSH 連線，下次重新部署獨立的 session。

機台上啟動的 PowerShell 視窗留在前景：顯示 sshd 的登入／斷線事件，以及 SSH 裡打的每個指令和輸出（terminal.log）。正常結束會關閉本機子程序，遠端監控腳本結束自己的 ft 和 sshd。異常斷線時，lease 超過 120 秒未更新會停止遠端程序。主機金鑰、授權公鑰、記錄全部保留，不自動刪除。

本機臨時目錄在 `%TEMP%\UvncFtpBridge\SshClient\<隨機ID>`（和機台端分開，連 127.0.0.1 自我測試時才不會撞同一個資料夾），本次臨時私鑰留在這裡，需要時自行清理。

`UVNC_SOCKS5_PROXY` 仍控制 VNC 如何連機台，可經既有 RDP File-Tunnel。SSH 本身只連本機隨機埠，不佔用 RDP File-Tunnel 的 1081 埠。

## 既有 sshd 相容模式

```cmd
UvncFtpBridge.exe --ssh Ussr@192.168.1.45 -p VNC密碼 --ssh-port 22
```

明確指定 `--ssh-port` 才改用機台既有的 sshd，不部署可攜 server。這個模式使用機台原本的帳密、金鑰與主機信任設定。

## 驗證範圍

已在 192.168.1.45 驗證：透過 VNC 上傳並從桌面啟動本次的可攜 sshd，以臨時金鑰登入成功，`hostname`、`whoami` 正常回傳。確認程序來自本次隨機資料夾，而不是 System32 的 sshd。

可攜 sshd 模式也已驗證：`--admin`（舊版為 `root@`）在 UAC 確認後回傳 `ADMIN=True`；一般模式出現真正 cmd 的版本橫幅與提示字元，預先送進 stdin 的 `echo PORTABLE_SHELL_OK` 成功，`exit 7` 會把結束碼 7 傳回本機。這些測試不使用機台既有 sshd 的帳密或 authorized_keys。

先前既有 sshd 模式另外驗證過分段輸出。測試工具擷取真正 cmd 的中文橫幅時有編碼顯示問題，中文終端的端到端編碼仍待處理。

目前建連可能需要數十秒；每次都會上傳二進位檔。實際鍵盤體驗、Ctrl+C 傳到遠端、SFTP、中文編碼、大量資料與更多 Windows 版本仍需測試，不能把使用 OpenSSH 等同於傳輸通道已通過所有相容性測試。
