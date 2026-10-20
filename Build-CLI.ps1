[CmdletBinding()]
param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $outputPath = if ($FrameworkDependent) { Join-Path $PSScriptRoot 'dist\FrameworkDependent' } else { Join-Path $PSScriptRoot 'dist' }
    $publishArguments = @('publish', 'UvncFtpBridge.csproj', '-c', 'Release', '-r', 'win-x64', '-p:DebugType=none', '-o', $outputPath)
    if ($FrameworkDependent) {
        $publishArguments += @('--self-contained', 'false', '-p:PublishSingleFile=false', '-p:PublishAot=false')
    } else {
        $publishArguments += @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
    & (Join-Path $outputPath 'UvncFtpBridge.exe') --selftest
    if ($LASTEXITCODE -ne 0) { throw 'CLI self-test failed.' }
    if ($FrameworkDependent) {
        @'
UltraVNC CLI - framework-dependent / Windows x64

This small build does not include the .NET runtime.
Install .NET 9 Runtime x64 before running it:
https://dotnet.microsoft.com/en-us/download/dotnet/9.0

The .NET Runtime is sufficient; the SDK is not required.
Keep the EXE, DLL, deps.json and runtimeconfig.json files together.

UvncFtpBridge.exe --help
'@ | Set-Content -LiteralPath (Join-Path $outputPath 'README-FrameworkDependent.txt') -Encoding utf8
    }
    Write-Output "CLI output: $outputPath"
} finally {
    Pop-Location
}
