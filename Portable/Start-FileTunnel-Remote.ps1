param([string]$ClientDriveOrPath = 'C')
$ErrorActionPreference = 'Stop'

function Read-Marker([string]$path) {
    try { return [IO.File]::ReadAllText($path).Trim() } catch { return $null }
}

try {
    $ft = Join-Path $PSScriptRoot 'ft.exe'
    if (-not (Test-Path -LiteralPath $ft)) { throw 'Place ft.exe beside this script.' }

    $explicitPath = [IO.Path]::IsPathRooted($ClientDriveOrPath) -and $ClientDriveOrPath.Length -gt 2
    if (-not $explicitPath) {
        $drive = $ClientDriveOrPath.TrimEnd(':')
        if ($drive -notmatch '^[A-Za-z]$') { throw 'Pass a drive letter such as C, or the complete redirected TEMP path.' }
        $profiles = "\\tsclient\$drive\Users"
    }

    $extraOptions = @()
    $optionsFile = Join-Path $PSScriptRoot 'ft-options.txt'
    if (Test-Path -LiteralPath $optionsFile) {
        foreach ($line in [IO.File]::ReadAllLines($optionsFile)) {
            $line = $line.Trim()
            if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
            $extraOptions += $line -split '\s+'
        }
    }
    if ($extraOptions.Count -gt 0) { Write-Host ('Extra ft.exe options (ft-options.txt): ' + ($extraOptions -join ' ')) }

    Write-Host 'Waiting for the local WinSCP File-Tunnel launcher. Close this window or press Ctrl+C to stop.'
    # Each pass serves one local launcher session; when it ends, go back to waiting for the next one.
    while ($true) {
        $shared = if ($explicitPath) { $ClientDriveOrPath } else { $null }
        $lastStatus = ''
        while ($true) {
            if ($explicitPath) {
                $ready = Test-Path -LiteralPath (Join-Path $shared 'active.marker')
                $status = if ($ready) { '' } else { "Waiting for local tunnel at $shared" }
            } elseif (-not (Test-Path -LiteralPath $profiles)) {
                $status = "Waiting for $profiles. Enable drive redirection in mstsc and reconnect."
                $ready = $false
            } else {
                $found = @(Get-ChildItem -LiteralPath $profiles -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    $candidate = Join-Path $_.FullName 'AppData\Local\Temp\UvncFtpBridge\FileTunnel'
                    if (Test-Path -LiteralPath (Join-Path $candidate 'active.marker')) { $candidate }
                })
                $ready = $found.Count -eq 1
                if ($ready) { $shared = $found[0]; $status = '' }
                elseif ($found.Count -gt 1) { $status = "Multiple active local TEMP tunnels found under $profiles. Close the extra ones, or restart this script with the complete redirected TEMP path." }
                else { $status = "Waiting for local tunnel under $profiles. Start-WinSCP-FileTunnel on the local PC." }
            }
            if ($ready) { break }
            if ($status -ne $lastStatus) { Write-Host $status; $lastStatus = $status }
            Start-Sleep -Seconds 2
        }

        $marker = Join-Path $shared 'active.marker'
        $session = Read-Marker $marker
        Write-Host ''
        Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Connected to local tunnel: $shared"
        Write-Host 'Keep this window open while using WinSCP.'
        $ftArgs = @('--read', ('"' + (Join-Path $shared 'to-remote.dat') + '"'), '--write', ('"' + (Join-Path $shared 'to-local.dat') + '"')) + $extraOptions
        $process = Start-Process -FilePath $ft -ArgumentList $ftArgs -NoNewWindow -PassThru
        $null = $process.Handle # keep the handle so ExitCode is available after ft.exe exits
        try {
            while (-not $process.HasExited) {
                # The marker disappears when the local launcher closes (or RDP drops); a different
                # marker content means the local side already restarted with fresh tunnel files.
                $current = Read-Marker $marker
                if ($null -eq $current -or $current -ne $session) { break }
                Start-Sleep -Milliseconds 500
            }
            if ($process.HasExited) {
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] ft.exe exited (code $($process.ExitCode))."
            } else {
                Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Local tunnel closed."
            }
        } finally {
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
            $process.Dispose()
        }
        Write-Host 'Waiting for the local WinSCP File-Tunnel launcher again.'
        Start-Sleep -Seconds 2
    }
} catch {
    [Console]::Error.WriteLine("File-Tunnel remote error: $($_.Exception.Message)")
    exit 1
}
