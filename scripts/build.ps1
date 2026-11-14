param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$RunTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

try {
    $hasSdk = $false
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        $sdks = & dotnet --list-sdks 2>$null
        if ($sdks) { $hasSdk = $true }
    }
    if (-not $hasSdk -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) {
        $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
        $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
    }

    Write-Host "==> Restoring & Building Ripple.sln ($Configuration)..." -ForegroundColor Cyan
    & dotnet build "Ripple.sln" -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    if ($RunTests) {
        Write-Host "==> Running xUnit Suite & 24h Soak Test..." -ForegroundColor Cyan
        & dotnet test "Ripple.sln" -c $Configuration --no-build --verbosity normal
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
    }

    Write-Host "==> Build complete!" -ForegroundColor Green
}
finally {
    Pop-Location
}
