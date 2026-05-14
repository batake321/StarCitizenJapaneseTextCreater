param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot

if (-not $OutputDir) {
    $OutputDir = Join-Path $ProjectDir "db_backup"
}

Write-Host "=== DB Backup Export ===" -ForegroundColor Cyan
Write-Host "Output: $OutputDir"

# Build first
Write-Host "Building..." -ForegroundColor Yellow
dotnet build -c Debug --quiet $ProjectDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Run export via app's --export-backup command
$exe = Join-Path $ProjectDir "bin\Debug\net8.0-windows\StarCitizenJapaneseTextCreater.exe"
& $exe --export-backup $OutputDir

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "Backup files saved to: $OutputDir"
Write-Host "Commit and push to share with teammates."
