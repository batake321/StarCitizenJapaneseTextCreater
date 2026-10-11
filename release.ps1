param(
    [string]$BackupZip,

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

# 1.5. Bundle DB files from WorkDir
Write-Host "=== Bundle DB ===" -ForegroundColor Cyan
$workDir = "D:\temp"
# 同梱してよい DB はこの 3 つだけ。
# 所持船・My Hangar・所持コンポーネント・装備の設定は trade_cache.db に入っており、これは個人データなので絶対に同梱しない
$allowedDbs = @("translations.db", "gamedata_cache.db", "equipment_cache.db")
foreach ($db in $allowedDbs) {
    $src = Join-Path $workDir $db
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $PublishDir $db) -Force
        Write-Host "  Bundled: $db ($("{0:N1} MB" -f ((Get-Item $src).Length / 1MB)))" -ForegroundColor Green
    } else {
        Write-Host "  Skip: $db (not found in $workDir)" -ForegroundColor Yellow
    }
}

# 許可した DB 以外が publish に紛れていたら ZIP を作らずに止める (個人データの同梱防止)
$strayDbs = @(Get-ChildItem $PublishDir -Filter *.db -File | Where-Object { $allowedDbs -notcontains $_.Name })
if ($strayDbs.Count -gt 0) {
    throw "同梱してはいけない DB が publish にあります: $($strayDbs.Name -join ', ')"
}
# 個人データ (trade_cache.db とその作業用ファイル -wal / -shm / -journal) は、サブフォルダも含めてどこにあっても止める
$personalNames = @("trade_cache.db", "trade_cache.db-wal", "trade_cache.db-shm", "trade_cache.db-journal")
$personalFiles = @(Get-ChildItem $PublishDir -Recurse -File | Where-Object { $personalNames -contains $_.Name })
if ($personalFiles.Count -gt 0) {
    throw "個人データが publish にあります: $($personalFiles.FullName -join ', ')"
}
# インストーラー (*Setup*.exe) が publish に紛れていたら止める (インストーラーにも ZIP にも二重に入るのを防ぐ)
$strayInstallers = @(Get-ChildItem $PublishDir -Recurse -File -Filter *.exe | Where-Object { $_.Name -like "*Setup*.exe" })
if ($strayInstallers.Count -gt 0) {
    throw "インストーラーが publish にあります: $($strayInstallers.FullName -join ', ')"
}
Write-Host "  Personal-data check: OK (bundled DBs = $($allowedDbs -join ', '))" -ForegroundColor Green

# 1.9. .NET 10 Desktop Runtime (x64) の最新版を確かめる
# Microsoft 公式のリリース情報 (releases.json) の latest-release から windowsdesktop / win-x64 の .exe の URL と SHA-512 を取り、
# ダウンロードして SHA-512 と Authenticode 署名を確かめてから SHA-256 を求め、インストーラーに /D で渡す。
# どこかで失敗したらリリースを止める。ダウンロードしたファイルは installer-output\dotnet (git に入らない) に置く
Write-Host "=== .NET 10 Desktop Runtime ===" -ForegroundColor Cyan
$dotnetReleasesUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json"
$dotnetMeta = Invoke-RestMethod -Uri $dotnetReleasesUrl
$dotnetLatest = $dotnetMeta.'latest-release'
$dotnetRelease = @($dotnetMeta.releases | Where-Object { $_.'release-version' -eq $dotnetLatest }) | Select-Object -First 1
if (-not $dotnetRelease) { throw ".NET のリリース情報に latest-release ($dotnetLatest) が見つかりません" }
$dotnetVersion = $dotnetRelease.windowsdesktop.version
$dotnetFile = @($dotnetRelease.windowsdesktop.files | Where-Object { $_.rid -eq "win-x64" -and $_.name -eq "windowsdesktop-runtime-win-x64.exe" }) | Select-Object -First 1
if (-not $dotnetVersion -or -not $dotnetFile -or -not $dotnetFile.url -or -not $dotnetFile.hash) {
    throw ".NET のリリース情報から windowsdesktop / win-x64 の .exe の URL と SHA-512 を取れません"
}
$dotnetUrl = [string]$dotnetFile.url
if ($dotnetUrl -notmatch '^https://builds\.dotnet\.microsoft\.com/') { throw ".NET のダウンロード元が想定外です: $dotnetUrl" }
$dotnetDir = Join-Path $ProjectDir "installer-output\dotnet"
New-Item -ItemType Directory -Force $dotnetDir | Out-Null
$dotnetPath = Join-Path $dotnetDir ([System.IO.Path]::GetFileName(([Uri]$dotnetUrl).AbsolutePath))
$prevProgress = $ProgressPreference
$ProgressPreference = "SilentlyContinue"
try { Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetPath -UseBasicParsing } finally { $ProgressPreference = $prevProgress }
$dotnetSha512 = (Get-FileHash $dotnetPath -Algorithm SHA512).Hash
if ($dotnetSha512 -ne ([string]$dotnetFile.hash).ToUpperInvariant()) {
    throw ".NET のランタイムの SHA-512 がリリース情報と一致しません: $dotnetPath"
}
$dotnetSig = Get-AuthenticodeSignature $dotnetPath
if ($dotnetSig.Status -ne 'Valid' -or $dotnetSig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw ".NET のランタイムの署名が正しくありません: $($dotnetSig.Status) / $($dotnetSig.SignerCertificate.Subject)"
}
$dotnetSha256 = (Get-FileHash $dotnetPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "  .NET $dotnetVersion : $dotnetUrl" -ForegroundColor Green
Write-Host "  SHA-512 OK / 署名 OK ($($dotnetSig.SignerCertificate.Subject)) / SHA-256 $dotnetSha256" -ForegroundColor Green

# 2. Build installer (Inno Setup)
# installer.iss は publish\* を {app} に入れる (trade_cache.db とその作業用ファイルは除外)。出力は installer-output\SCJPTextCreator-v<Version>-Setup.exe
# Setup.exe は publish には置かない (置くと次のインストーラーに紛れ込むため)。ZIP にだけ足す
Write-Host "=== Build Installer ===" -ForegroundColor Cyan
$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    throw "Inno Setup のコンパイラ (ISCC.exe) が見つかりません。Inno Setup 6 をインストールしてください。探した場所: $($isccCandidates -join ', ')"
}
$InstallerName = "SCJPTextCreator-v$Version-Setup.exe"
$InstallerPath = Join-Path $ProjectDir "installer-output\$InstallerName"
if (Test-Path $InstallerPath) { Remove-Item $InstallerPath -Force }
& $iscc /Qp "/DMyAppVersion=$Version" "/DDotNetVersion=$dotnetVersion" "/DDotNetUrl=$dotnetUrl" "/DDotNetSha256=$dotnetSha256" (Join-Path $ProjectDir "installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer build failed (ISCC exit code $LASTEXITCODE)" }
if (-not (Test-Path $InstallerPath)) { throw "Installer not found: $InstallerPath" }
$setupSizeMB = "{0:N2} MB" -f ((Get-Item $InstallerPath).Length / 1MB)
Write-Host "Installer: $InstallerName ($setupSizeMB)" -ForegroundColor Green

# 2.5. Create ZIP (publish 一式 + Setup.exe を一番上の階層に)
Write-Host "=== Create ZIP ===" -ForegroundColor Cyan
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$PublishDir\*" -DestinationPath $ZipPath
Compress-Archive -Path $InstallerPath -DestinationPath $ZipPath -Update

# ZIP の中身を確かめる: 個人データが無いこと、Setup.exe が一番上にあること
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipArchive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entryNames = @($zipArchive.Entries | ForEach-Object { $_.FullName })
} finally {
    $zipArchive.Dispose()
}
$personalInZip = @($entryNames | Where-Object { $personalNames -contains (($_ -replace '\\', '/') -split '/')[-1] })
if ($personalInZip.Count -gt 0) {
    Remove-Item $ZipPath -Force
    throw "個人データが ZIP に入っています (ZIP は削除しました): $($personalInZip -join ', ')"
}
if ($entryNames -notcontains $InstallerName) {
    throw "ZIP の一番上に $InstallerName がありません"
}
$sizeMB = "{0:N2} MB" -f ((Get-Item $ZipPath).Length / 1MB)
Write-Host "ZIP: $ZipName ($sizeMB, $InstallerName を同梱)" -ForegroundColor Green

# 3. Git commit & push
Write-Host "=== Git Commit & Push ===" -ForegroundColor Cyan
git add -A
$status = git status --porcelain
if ($status) {
    $commitMsg = if ($Message) { $Message } else { "Release v$Version" }
    $commitMsg += "`n`nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
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


if ($BackupZip) {
    $uploadUrl2 = "https://uploads.github.com/repos/$Repo/releases/$releaseId/assets?name=$(Split-Path $BackupZip -Leaf)"
    Invoke-RestMethod -Uri $uploadUrl2 -Method Post -Headers $uploadHeaders -InFile $BackupZip
    Write-Host "Backup asset uploaded" -ForegroundColor Green
}

$asset = Invoke-RestMethod -Uri $uploadUrl -Method Post -Headers $uploadHeaders -InFile $ZipPath

Write-Host "Asset uploaded: $($asset.browser_download_url)" -ForegroundColor Green

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "Release: $($release.html_url)"
Write-Host "Download: $($asset.browser_download_url)"


