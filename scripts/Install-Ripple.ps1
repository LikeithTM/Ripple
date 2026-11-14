param(
    [switch]$AddToPath,
    [switch]$StartWithWindows
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\Ripple"

Write-Host "==> Building & Installing Ripple into $InstallRoot..." -ForegroundColor Cyan
& "$PSScriptRoot\package-portable.ps1" -Configuration Release

$stage = Join-Path $RepoRoot "dist\Ripple-1.0.0-win-x64"
if (Test-Path $InstallRoot) { Remove-Item $InstallRoot -Recurse -Force }
Copy-Item $stage -Destination $InstallRoot -Recurse -Force

if ($AddToPath) {
    $cliRoot = Join-Path $InstallRoot "cli"
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$cliRoot*") {
        [Environment]::SetEnvironmentVariable("Path", "$userPath;$cliRoot", "User")
        Write-Host "==> Added $cliRoot to User PATH (ripple CLI ready)." -ForegroundColor Green
    }
}

if ($StartWithWindows) {
    Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Ripple" -Value "`"$InstallRoot\Ripple.exe`""
}

Write-Host "==> Installed! Launch Ripple from $InstallRoot\Ripple.exe" -ForegroundColor Green
