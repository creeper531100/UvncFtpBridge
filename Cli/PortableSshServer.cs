namespace UvncFtpBridge.Cli;

internal static class PortableSshServer
{
    internal static readonly string[] Files =
    ["sshd.exe", "sshd-auth.exe", "sshd-session.exe", "ssh-shellhost.exe", "ssh-keygen.exe", "sftp-server.exe", "libcrypto.dll"];

    internal const string ShellScript = """
        [Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
        $OutputEncoding = [Console]::OutputEncoding
        if (Get-Module -ListAvailable PSReadLine) {
            Import-Module PSReadLine
            Set-PSReadLineOption -HistorySavePath (Join-Path $PSScriptRoot 'history.txt') -HistorySaveStyle SaveNothing
        }
        Start-Transcript -LiteralPath (Join-Path $PSScriptRoot 'terminal.log') -Force | Out-Null
        if ($env:SSH_ORIGINAL_COMMAND) {
            Write-Host ('> ' + $env:SSH_ORIGINAL_COMMAND)
            & $env:ComSpec /d /s /c $env:SSH_ORIGINAL_COMMAND | Out-Default
            $code = $LASTEXITCODE
            Stop-Transcript | Out-Null
            # -NoExit keeps the host alive after a plain "exit" from the script; end the process instead.
            [Environment]::Exit($code)
        }
        Write-Host 'PowerShell session is visible on the machine console.'
        """;

    // Debug mode supports a single connection as the desktop user, without installing a service.
    // Everything (keys, config, logs) lives in %TEMP%\UvncFtpBridge\SshTunnel\<id>; nothing is deleted afterwards.
    internal static string Bootstrap(string id) => $$"""
        $ErrorActionPreference = 'Stop'
        $dir = $PSScriptRoot
        Set-Location -LiteralPath $dir
        # Run only once per session; a stray re-run (e.g. Win+R history) must not touch existing keys.
        if (Test-Path -LiteralPath "$dir\started") { Write-Host 'This SSH session was already started; nothing to do.'; Start-Sleep 3; exit }
        New-Item -ItemType File -Path "$dir\started","$dir\launched" -Force | Out-Null
        [Console]::OutputEncoding = New-Object Text.UTF8Encoding $false
        $host.UI.RawUI.WindowTitle = 'UvncFtpBridge SSH - ' + $env:USERNAME
        Write-Host 'REMOTE SSH SESSION - commands and output appear below' -ForegroundColor Cyan
        Write-Host ('Session directory: ' + $dir)
        $sshd = $null; $ft = $null
        $display = $null; $sshdLog = $null
        try {
            # The id is random, so finding start.ps1 under this user's %TEMP% proves $dir is that folder.
            if (-not (Test-Path -LiteralPath (Join-Path $env:TEMP 'UvncFtpBridge\SshTunnel\{{id}}\start.ps1'))) {
                throw ('Session folder is not under this user''s %TEMP% (' + $env:TEMP + '). Use the real Windows account name.')
            }
            $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            & icacls.exe $dir /inheritance:r /grant:r ('*' + $sid + ':(OI)(CI)F') '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /Q | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Cannot secure SSH session directory.' }
            & "$dir\ssh-keygen.exe" -q -t ed25519 -N '""' -f "$dir\host_key"
            if ($LASTEXITCODE -ne 0) { throw 'Cannot generate SSH host key.' }
            $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback),0
            $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
            $path = $dir.Replace('\','/')
            $config = @(
                "Port $port", 'ListenAddress 127.0.0.1',
                ('HostKey "' + $path + '/host_key"'), ('PidFile "' + $path + '/sshd.pid"'),
                ('AuthorizedKeysFile "' + $path + '/authorized_keys"'), 'StrictModes yes',
                'PubkeyAuthentication yes', 'PasswordAuthentication no',
                'KbdInteractiveAuthentication no', 'AuthenticationMethods publickey',
                'LoginGraceTime 0', 'LogLevel VERBOSE',
                ('Subsystem sftp "' + $path + '/sftp-server.exe"'),
                ('ForceCommand powershell.exe -NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -File "' + $path + '/shell.ps1"')
            )
            [IO.File]::WriteAllLines("$dir\sshd_config", $config, (New-Object Text.UTF8Encoding $false))
            $sshd = Start-Process -FilePath "$dir\sshd.exe" -ArgumentList ('-ddd -e -f "' + $dir + '\sshd_config"') -PassThru -NoNewWindow -WorkingDirectory $dir -RedirectStandardOutput "$dir\sshd.out" -RedirectStandardError "$dir\sshd.err"
            Start-Sleep -Milliseconds 800
            if ($sshd.HasExited) { throw ('sshd startup failed (exit ' + $sshd.ExitCode + '): ' + ((Get-Content "$dir\sshd.err" -Tail 5) -join "`n")) }
            $ft = Start-Process -FilePath "$dir\ft.exe" -ArgumentList ('--upload-download --tunnel-timeout 60000 --write "' + $dir + '\to-local.dat" --read "' + $dir + '\to-remote.dat"') -PassThru -NoNewWindow -WorkingDirectory $dir -RedirectStandardOutput "$dir\ft.log" -RedirectStandardError "$dir\ft.err"
            @("$port", $env:USERNAME) | Set-Content -LiteralPath "$dir\server.ready.tmp" -Encoding utf8
            Move-Item -LiteralPath "$dir\server.ready.tmp" -Destination "$dir\server.ready"
            while (-not $sshd.HasExited -and -not $ft.HasExited) {
                Start-Sleep -Milliseconds 300
                if (!$display -and (Test-Path -LiteralPath "$dir\terminal.log")) {
                    $stream = [IO.File]::Open("$dir\terminal.log", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                    $display = New-Object IO.StreamReader $stream
                }
                if ($display) { [Console]::Write($display.ReadToEnd()) }
                # sshd itself runs in this window's process tree; echo its connection events, not the -ddd noise.
                if (!$sshdLog -and (Test-Path -LiteralPath "$dir\sshd.err")) {
                    $sshdLog = New-Object IO.StreamReader ([IO.File]::Open("$dir\sshd.err", [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite))
                }
                if ($sshdLog) {
                    while (($line = $sshdLog.ReadLine()) -ne $null) {
                        if ($line -match 'Server listening|Accepted |Starting session|Received disconnect|Connection closed|error:|fatal:') { Write-Host ('[sshd] ' + $line) -ForegroundColor DarkYellow }
                    }
                }
                $lease = Get-Item -LiteralPath "$dir\lease" -ErrorAction SilentlyContinue
                if (!$lease -or ((Get-Date).ToUniversalTime() - $lease.LastWriteTimeUtc).TotalSeconds -gt 120) { break }
            }
        } catch {
            $_ | Out-String | Set-Content -LiteralPath "$dir\bootstrap.error" -Encoding utf8
            Write-Host ($_ | Out-String) -ForegroundColor Red
        }
        finally {
            if ($display) { [Console]::Write($display.ReadToEnd()); $display.Dispose() }
            if ($sshdLog) { $sshdLog.Dispose() }
            foreach ($p in @($ft, $sshd)) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
            Write-Host 'SSH session ended. Files kept in the session directory above.' -ForegroundColor Cyan
        }
        """;
}
