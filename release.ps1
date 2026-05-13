param(
    [Parameter(Mandatory=$true)]
    [string]$Version,

    [string]$Message = ""
)

$ErrorActionPreference = "Stop"
$ProjectDir = $PSScriptRoot
$ProjectName = "StarCitizenJapaneseTextCreater"
$PublishDir = "$ProjectDir\publish"
$ZipName = "$ProjectName-v$Version-win-x64.zip"
$ZipPath = Join-Path $ProjectDir $ZipName
$Repo = "batake321/StarCitizenJapaneseTextCreater"

Set-Location $ProjectDir

# 1. Build & Publish
Write-Host "=== Build & Publish ===" -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained false -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Build OK" -ForegroundColor Green

# 2. Create ZIP
Write-Host "=== Create ZIP ===" -ForegroundColor Cyan
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipPath
$sizeMB = "{0:N2} MB" -f ((Get-Item $ZipPath).Length / 1MB)
Write-Host "ZIP: $ZipName ($sizeMB)" -ForegroundColor Green

# 3. Git commit & push
Write-Host "=== Git Commit & Push ===" -ForegroundColor Cyan
git add -A
$status = git status --porcelain
if ($status) {
    $commitMsg = if ($Message) { $Message } else { "Release v$Version" }
    $commitMsg += "`n`nCo-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>"
    git commit -m $commitMsg
    git push origin main
    Write-Host "Push OK" -ForegroundColor Green
} else {
    Write-Host "No changes to commit" -ForegroundColor Yellow
}

# 4. GitHub Release (via API)
Write-Host "=== GitHub Release ===" -ForegroundColor Cyan
$credFile = Join-Path $env:USERPROFILE ".git-credentials"
$token = ""
if (Test-Path $credFile) {
    $line = Get-Content $credFile | Where-Object { $_ -match "github.com" } | Select-Object -First 1
    if ($line -match "https://[^:]+:([^@]+)@github.com") {
        $token = $Matches[1]
    }
}
if (-not $token) {
    Write-Host "GitHub token not found in .git-credentials. Skipping release creation." -ForegroundColor Yellow
    Write-Host "Manually create release at: https://github.com/$Repo/releases/new" -ForegroundColor Yellow
    exit 0
}

$headers = @{
    "Authorization" = "token $token"
    "Content-Type"  = "application/json"
}

$releaseBody = if ($Message) { $Message } else { "Release v$Version" }
$bodyJson = @{
    tag_name   = "v$Version"
    name       = "v$Version"
    body       = $releaseBody
    draft      = $false
    prerelease = $false
} | ConvertTo-Json

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" -Method Post -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($bodyJson)) -ContentType "application/json; charset=utf-8"
$releaseId = $release.id
Write-Host "Release created: $($release.html_url)" -ForegroundColor Green

# 5. Upload ZIP asset
Write-Host "=== Upload Asset ===" -ForegroundColor Cyan
$uploadUrl = "https://uploads.github.com/repos/$Repo/releases/$releaseId/assets?name=$ZipName"
$uploadHeaders = @{
    "Authorization" = "token $token"
    "Content-Type"  = "application/zip"
}
$asset = Invoke-RestMethod -Uri $uploadUrl -Method Post -Headers $uploadHeaders -InFile $ZipPath
Write-Host "Asset uploaded: $($asset.browser_download_url)" -ForegroundColor Green

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "Release: $($release.html_url)"
Write-Host "Download: $($asset.browser_download_url)"
