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
dotnet build -c Debug -v quiet $ProjectDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Run export via app's --export-backup command
# TargetFramework 名 (net8.0-windows10.0.19041.0 等) が変わっても追従するよう、ビルド成果物を探して一番新しいものを使う
# (固定パスにしていたため、TFM 変更後は 3 か月前の古い exe を実行し続けていた)
$exe = Get-ChildItem -Path (Join-Path $ProjectDir "bin\Debug") -Filter "StarCitizenJapaneseTextCreater.exe" -Recurse -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw "ビルド成果物が見つかりません: bin\Debug 配下に StarCitizenJapaneseTextCreater.exe がありません" }
Write-Host "Exe: $($exe.FullName) ($($exe.LastWriteTime))" -ForegroundColor DarkGray

# WinExe なので & では待たない。明示的に待ち、終了コードを確認する
$proc = Start-Process -FilePath $exe.FullName -ArgumentList @("--export-backup", $OutputDir) -PassThru -NoNewWindow
$proc.WaitForExit()
if ($proc.ExitCode -ne 0) { throw "Export failed (exit $($proc.ExitCode))" }

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "Backup files saved to: $OutputDir"
Write-Host "Commit and push to share with teammates."
