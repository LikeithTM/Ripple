param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$SelfContained
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

    $distDir = Join-Path $RepoRoot "dist"
    $stageDir = Join-Path $distDir "Ripple-$Version-win-x64"
    $zipPath = Join-Path $distDir "Ripple-$Version-portable-win-x64.zip"

    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    $scFlag = if ($SelfContained) { "true" } else { "false" }

    Write-Host "==> Publishing Ripple.App ($Configuration, SelfContained=$scFlag)..." -ForegroundColor Cyan
    dotnet publish "src/Ripple.App/Ripple.App.csproj" -c $Configuration -r win-x64 --self-contained $scFlag -o $stageDir

    Write-Host "==> Publishing Ripple.Cli (cli\ripple.exe)..." -ForegroundColor Cyan
    $cliStage = Join-Path $stageDir "cli"
    dotnet publish "src/Ripple.Cli/Ripple.Cli.csproj" -c $Configuration -r win-x64 --self-contained $scFlag -o $cliStage
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed" }

    Copy-Item "plugins" -Destination (Join-Path $stageDir "plugins") -Recurse -Force
    Copy-Item "README.md", "LICENSE" -Destination $stageDir -Force

    Write-Host "==> Compressing portable zip -> $zipPath" -ForegroundColor Cyan
    Compress-Archive -Path "$stageDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "==> Portable package ready: $zipPath" -ForegroundColor Green
}
finally {
    Pop-Location
}
