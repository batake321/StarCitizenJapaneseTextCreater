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

# このプロジェクトのフォルダ (publish・bin など) から起動しているアプリがあれば止める (publish の exe を置き換えられないため)。
# インストール先など、ほかの場所から起動しているものは止めない。作業フォルダ (AppData) の DB は下の -wal の確かめと quick_check で守る
$running = @(Get-Process -Name $ProjectName -ErrorAction SilentlyContinue | Where-Object {
    -not $_.Path -or $_.Path.StartsWith($ProjectDir + '\', [StringComparison]::OrdinalIgnoreCase) })
if ($running.Count -gt 0) {
    $where = ($running | ForEach-Object { if ($_.Path) { $_.Path } else { "PID $($_.Id)" } }) -join ', '
    throw "アプリを閉じてから実行してください (起動中: $where)"
}

# 1. Build & Publish
Write-Host "=== Build & Publish ===" -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained false -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Build OK" -ForegroundColor Green

# 1.5. Bundle DB files from WorkDir
Write-Host "=== Bundle DB ===" -ForegroundColor Cyan
$workDir = Join-Path $ProjectDir "AppData"
# 同梱してよい DB はこの 3 つだけ。
# 所持船・My Hangar・所持コンポーネント・装備の設定は trade_cache.db に入っており、これは個人データなので絶対に同梱しない
$allowedDbs = @("translations.db", "gamedata_cache.db", "equipment_cache.db")
# gamedata_cache.db と translations.db は WAL (journal_mode=WAL) で使う。書き込みはまず <名前>-wal に入り、DB の本体だけを写すと中身が抜けるので、
# -wal が 0 バイトでなければ止める (アプリを閉じると、閉じるときのチェックポイントで -wal が空になる)
foreach ($db in @("gamedata_cache.db", "translations.db")) {
    $wal = Join-Path $workDir "$db-wal"
    if ((Test-Path $wal) -and ((Get-Item $wal).Length -gt 0)) {
        throw "アプリを一度起動して閉じ直してから実行してください ($wal に、まだ DB に書き戻していない中身があります)"
    }
}
foreach ($db in $allowedDbs) {
    $src = Join-Path $workDir $db
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $PublishDir $db) -Force
        Write-Host "  Bundled: $db ($("{0:N1} MB" -f ((Get-Item $src).Length / 1MB)))" -ForegroundColor Green
    } else {
        Write-Host "  Skip: $db (not found in $workDir)" -ForegroundColor Yellow
    }
}

# 同梱する gamedata_cache.db と translations.db を DELETE 方式の 1 つのファイルにする (WAL の見出しのままだと、書けないフォルダ
# (インストール先) で読み取り専用で開けない)。publish の exe の --prepare-bundled-db が DELETE にして、見出し 1/1・quick_check ok・横のファイルが
# 無いことを確かめる。だめなら止める
$prepareDbs = @(@("gamedata_cache.db", "translations.db") | ForEach-Object { Join-Path $PublishDir $_ } | Where-Object { Test-Path $_ })
if ($prepareDbs.Count -gt 0) {
    $exePath = Join-Path $PublishDir "$ProjectName.exe"
    $outFile = [System.IO.Path]::GetTempFileName()
    $proc = Start-Process -FilePath $exePath -ArgumentList (@("--prepare-bundled-db") + ($prepareDbs | ForEach-Object { "`"$_`"" })) -PassThru -NoNewWindow -Wait -RedirectStandardOutput $outFile
    Get-Content $outFile -Encoding UTF8 | ForEach-Object { Write-Host "  $_" }
    Remove-Item $outFile -ErrorAction SilentlyContinue
    if ($proc.ExitCode -ne 0) { throw "同梱する DB を DELETE 方式にできませんでした (終了コード $($proc.ExitCode))" }
    foreach ($p in $prepareDbs) {
        # 見出しの 18・19 バイト目が 1/1 (DELETE などの方式。2/2 は WAL) か、ここでも確かめる
        $fs = [System.IO.File]::OpenRead($p)
        try { $hdr = New-Object byte[] 100; [void]$fs.Read($hdr, 0, 100) } finally { $fs.Dispose() }
        if ($hdr[18] -ne 1 -or $hdr[19] -ne 1) { throw "同梱する DB の見出しが DELETE 方式ではありません: $p ($($hdr[18])/$($hdr[19]))" }
        foreach ($sfx in @("-wal", "-shm", "-journal")) {
            if (Test-Path "$p$sfx") { throw "同梱する DB の横のファイルが残っています: $p$sfx" }
        }
    }
    Write-Host "  Bundled DB journal mode: DELETE (header 1/1, quick_check ok)" -ForegroundColor Green
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

# 2.7. 自動更新の manifest と添付 (AppUpdater が読む)
# publish の全ファイル (個人データ trade_cache.db* とインストーラー *Setup*.exe を除く) の相対パス・大きさ・SHA-256 と、取りに行く URL を
# update-manifest.json に書く。各ファイルは gzip にして <SHA-256>.gz の名前でこの版のリリースに載せる。ただし 1 つ前の版 (releases/latest) の
# manifest に同じ SHA-256 があれば、その URL (前の版のリリースの添付) をそのまま書き、載せ直さない (大きいファイルを毎回上げ直さないため)。
# 同梱の大きな DB は、前の版の DB からの差分 (exe の --make-db-delta) も作って載せ、前の版の manifest の差分の一覧に足して書く
# (アプリは隣り合う版どうしの差分を古い順に当てる)。差分が丸ごとの .gz より大きければ載せない。
# 載せるのは「4. GitHub Release」の後 (下の 5.5)。ここでは installer-output\update-v<Version> に作るだけ
Write-Host "=== Update manifest ===" -ForegroundColor Cyan
$UpdateDir = Join-Path $ProjectDir "installer-output\update-v$Version"
if (Test-Path $UpdateDir) { Remove-Item $UpdateDir -Recurse -Force }
$BlobDir = Join-Path $UpdateDir "assets"
New-Item -ItemType Directory -Force $BlobDir | Out-Null
$DownloadBase = "https://github.com/$Repo/releases/download/v$Version"

function New-GzFile([string]$src, [string]$dst) {
    $in = [System.IO.File]::OpenRead($src)
    try {
        $out = [System.IO.File]::Create($dst)
        try {
            $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        } finally { $out.Dispose() }
    } finally { $in.Dispose() }
}
function Expand-GzFile([string]$src, [string]$dst) {
    $in = [System.IO.File]::OpenRead($src)
    try {
        $gz = New-Object System.IO.Compression.GZipStream($in, [System.IO.Compression.CompressionMode]::Decompress)
        try {
            $out = [System.IO.File]::Create($dst)
            try { $gz.CopyTo($out) } finally { $out.Dispose() }
        } finally { $gz.Dispose() }
    } finally { $in.Dispose() }
}

# 1 つ前の版の manifest (無ければ、全部をこの版に載せる)
$prevManifest = $null
try {
    $latestRel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "User-Agent" = "release.ps1" }
    $mAsset = @($latestRel.assets | Where-Object { $_.name -eq "update-manifest.json" }) | Select-Object -First 1
    if ($mAsset) {
        $prevManifest = Invoke-RestMethod -Uri $mAsset.browser_download_url -Headers @{ "User-Agent" = "release.ps1" }
        Write-Host "  前の版の manifest: v$($prevManifest.version) ($(@($prevManifest.files).Count) ファイル)" -ForegroundColor Green
    } else {
        Write-Host "  前の版 ($($latestRel.tag_name)) に manifest が無いので、全部をこの版に載せます" -ForegroundColor Yellow
    }
} catch {
    Write-Host "  前の版の manifest を読めませんでした ($($_.Exception.Message))。全部をこの版に載せます" -ForegroundColor Yellow
}
$prevBySha = @{}
$prevByPath = @{}
if ($prevManifest) {
    foreach ($pf in @($prevManifest.files)) {
        $prevBySha[([string]$pf.sha256).ToLowerInvariant()] = $pf
        $prevByPath[([string]$pf.path).ToLowerInvariant()] = $pf
    }
}

# 必要な .NET の版 (publish の runtimeconfig.json の frameworks)
$rc = Get-Content (Join-Path $PublishDir "$ProjectName.runtimeconfig.json") -Raw | ConvertFrom-Json
$runtimes = @($rc.runtimeOptions.frameworks | ForEach-Object { [ordered]@{ name = $_.name; version = $_.version } })
if ($runtimes.Count -eq 0 -and $rc.runtimeOptions.framework) { $runtimes = @([ordered]@{ name = $rc.runtimeOptions.framework.name; version = $rc.runtimeOptions.framework.version }) }

$deltaDbNames = @("gamedata_cache.db", "translations.db", "equipment_cache.db")
$exeForDelta = Join-Path $PublishDir "$ProjectName.exe"
$newAssets = @{}   # 載せる添付の名前 → ファイル
$manifestFiles = @()
$publishFull = (Resolve-Path $PublishDir).Path.TrimEnd('\')
foreach ($file in (Get-ChildItem $PublishDir -Recurse -File | Sort-Object FullName)) {
    $rel = $file.FullName.Substring($publishFull.Length + 1).Replace('\', '/')
    if ($personalNames -contains $file.Name) { continue }
    if ($file.Name -like "*Setup*.exe") { continue }
    $sha = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $entry = [ordered]@{ path = $rel; size = $file.Length; sha256 = $sha; url = ""; gzsize = 0; deltas = @() }
    if ($prevBySha.ContainsKey($sha) -and $prevBySha[$sha].url) {
        $entry.url = [string]$prevBySha[$sha].url
        $entry.gzsize = [long]$prevBySha[$sha].gzsize
    } else {
        $gzName = "$sha.gz"
        $gzPath = Join-Path $BlobDir $gzName
        if (-not (Test-Path $gzPath)) { New-GzFile $file.FullName $gzPath }
        $newAssets[$gzName] = $gzPath
        $entry.url = "$DownloadBase/$gzName"
        $entry.gzsize = (Get-Item $gzPath).Length
    }
    # 同梱の大きな DB: 前の版の差分の一覧を引き継ぎ、前の版の DB からこの版の DB への差分を足す
    if ($deltaDbNames -contains $file.Name) {
        $prevEntry = $prevByPath[$rel.ToLowerInvariant()]
        $deltas = @()
        if ($prevEntry -and $prevEntry.deltas) { $deltas += @($prevEntry.deltas) }
        if ($prevEntry -and ([string]$prevEntry.sha256).ToLowerInvariant() -ne $sha -and $prevEntry.url) {
            $tmpGz = Join-Path $UpdateDir "prev-$($file.Name).gz"
            $tmpDb = Join-Path $UpdateDir "prev-$($file.Name)"
            try {
                $prevProgress2 = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
                try { Invoke-WebRequest -Uri ([string]$prevEntry.url) -OutFile $tmpGz -UseBasicParsing } finally { $ProgressPreference = $prevProgress2 }
                Expand-GzFile $tmpGz $tmpDb
                if ((Get-FileHash $tmpDb -Algorithm SHA256).Hash.ToLowerInvariant() -ne ([string]$prevEntry.sha256).ToLowerInvariant()) { throw "前の版の $($file.Name) の SHA-256 が manifest と違います" }
                $patchTmp = Join-Path $UpdateDir "delta-$($file.Name).tmp"
                $outFile = [System.IO.Path]::GetTempFileName()
                $proc = Start-Process -FilePath $exeForDelta -ArgumentList @("--make-db-delta", "`"$tmpDb`"", "`"$($file.FullName)`"", "`"$patchTmp`"") -PassThru -NoNewWindow -Wait -RedirectStandardOutput $outFile
                $deltaOut = (Get-Content $outFile -Encoding UTF8) -join " "
                Remove-Item $outFile -ErrorAction SilentlyContinue
                if ($proc.ExitCode -ne 0) { throw "差分を作れませんでした: $deltaOut" }
                $codec = ($deltaOut -split ' ')[1]
                $patchSha = (Get-FileHash $patchTmp -Algorithm SHA256).Hash.ToLowerInvariant()
                $patchSize = (Get-Item $patchTmp).Length
                if ($patchSize -lt $entry.gzsize) {
                    $patchName = "delta-$($prevEntry.sha256.Substring(0,16))-$($sha.Substring(0,16)).patch"
                    Move-Item $patchTmp (Join-Path $BlobDir $patchName) -Force
                    $newAssets[$patchName] = Join-Path $BlobDir $patchName
                    $deltas += [ordered]@{ fromVersion = [string]$prevManifest.version; toVersion = $Version; from = ([string]$prevEntry.sha256).ToLowerInvariant(); to = $sha;
                                           codec = $codec; url = "$DownloadBase/$patchName"; size = $patchSize; sha256 = $patchSha }
                    Write-Host ("  差分: {0} v{1}→v{2} {3:N1} MB (丸ごとの .gz {4:N1} MB)" -f $file.Name, $prevManifest.version, $Version, ($patchSize / 1MB), ($entry.gzsize / 1MB)) -ForegroundColor Green
                } else {
                    Remove-Item $patchTmp -Force
                    Write-Host "  差分: $($file.Name) は差分が丸ごとより大きいので載せません" -ForegroundColor Yellow
                }
            } catch {
                Write-Host "  差分: $($file.Name) の差分を作れませんでした ($($_.Exception.Message))。丸ごとだけを載せます" -ForegroundColor Yellow
            } finally {
                Remove-Item $tmpGz, $tmpDb -ErrorAction SilentlyContinue
            }
        }
        $entry.deltas = @($deltas)
    }
    $manifestFiles += $entry
}
# GitHub のリリースの添付は 1 つのリリースに 1000 件まで (ZIP・バックアップ・manifest の分を残す)
if ($newAssets.Count -gt 990) { throw "自動更新の添付が多すぎます ($($newAssets.Count) 件)。1 つのリリースに 1000 件までです" }
$manifest = [ordered]@{ version = $Version; runtimes = $runtimes; files = $manifestFiles }
$ManifestPath = Join-Path $UpdateDir "update-manifest.json"
[System.IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
$newBytes = ($newAssets.Values | ForEach-Object { (Get-Item $_).Length } | Measure-Object -Sum).Sum
Write-Host ("  manifest: {0} ファイル、この版に載せる添付 {1} 件 ({2:N1} MB)" -f $manifestFiles.Count, $newAssets.Count, ($newBytes / 1MB)) -ForegroundColor Green

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

# 5.5. 自動更新の添付 (<SHA-256>.gz と DB の差分) を載せてから、最後に manifest を載せる (manifest の URL の先が先にそろうように)
Write-Host "=== Upload update assets ===" -ForegroundColor Cyan
$n = 0
foreach ($name in ($newAssets.Keys | Sort-Object)) {
    $n++
    $upUrl = "https://uploads.github.com/repos/$Repo/releases/$releaseId/assets?name=$name"
    $upHeaders = @{ "Authorization" = "token $token"; "Content-Type" = "application/octet-stream" }
    Invoke-RestMethod -Uri $upUrl -Method Post -Headers $upHeaders -InFile $newAssets[$name] | Out-Null
    Write-Host "  [$n/$($newAssets.Count)] $name" -ForegroundColor Green
}
$manifestUpUrl = "https://uploads.github.com/repos/$Repo/releases/$releaseId/assets?name=update-manifest.json"
Invoke-RestMethod -Uri $manifestUpUrl -Method Post -Headers @{ "Authorization" = "token $token"; "Content-Type" = "application/json" } -InFile $ManifestPath | Out-Null
Write-Host "Update manifest uploaded" -ForegroundColor Green

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "Release: $($release.html_url)"
Write-Host "Download: $($asset.browser_download_url)"


